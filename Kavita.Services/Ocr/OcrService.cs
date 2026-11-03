using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Kavita.API.Database;
using Kavita.API.Services;
using Kavita.API.Services.SignalR;
using Kavita.Common;
using Kavita.Models.DTOs.Ocr;
using Kavita.Models.DTOs.SignalR;
using Kavita.Models.Entities;
using Kavita.Models.Entities.Enums;
using Kavita.Services.Scanner;
using Microsoft.Extensions.Logging;

namespace Kavita.Services.Ocr;

public class OcrService(ILogger<OcrService> logger, IUnitOfWork unitOfWork, IBookService bookService,
    IDirectoryService directoryService, IOcrSidecarClient sidecarClient, IEventHub eventHub,
    IOcrWorkerSignal workerSignal, ICacheService cacheService) : IOcrService
{
    /// <summary>
    /// A page with fewer non-whitespace characters than this is treated as a scan. Scans often carry a stray page number
    /// or a single glyph from the scanner's software
    /// </summary>
    public const int MinCharactersForText = 3;
    /// <summary>
    /// Times the sidecar may refuse a page before it's marked failed
    /// </summary>
    public const int MaxPageAttempts = 3;
    /// <summary>
    /// A file someone opened in the reader
    /// </summary>
    public const int ReaderPriority = 10;
    /// <summary>
    /// A series an admin queued by hand
    /// </summary>
    public const int AdminPriority = 20;

    /// <summary>
    /// The worker and a queued Hangfire job can both start a check. Two checks of one file would insert its pages twice
    /// </summary>
    private static readonly SemaphoreSlim DetectionLock = new(1, 1);

    public async Task MarkSeriesForCheckAsync(int seriesId, CancellationToken ct = default)
    {
        var files = await unitOfWork.OcrRepository.GetOcrFilesForSeriesAsync(seriesId, ct: ct);
        var needsCheck = false;

        foreach (var file in files)
        {
            if (file.OcrStatus == OcrStatus.Removed) continue;
            if (file.OcrStatus == OcrStatus.NotChecked)
            {
                needsCheck = true;
                continue;
            }

            if (!HasFileChangedSinceCheck(file)) continue;

            await unitOfWork.OcrRepository.DeletePagesForFileAsync(file.Id, ct);
            file.OcrStatus = OcrStatus.NotChecked;
            unitOfWork.MangaFileRepository.Update(file);
            needsCheck = true;
        }

        if (unitOfWork.HasChanges()) await unitOfWork.CommitAsync(ct);
        if (needsCheck) QueueDetection();
    }

    [AutomaticRetry(Attempts = 0)]
    public async Task DetectNewFilesAsync()
    {
        // Whoever holds the lock will reach these files, or the worker's next poll will
        if (!await DetectionLock.WaitAsync(0)) return;
        try
        {
            var fileIds = await unitOfWork.OcrRepository.GetFileIdsNeedingCheckAsync();
            var queuedAny = false;
            foreach (var fileId in fileIds)
            {
                queuedAny |= await DetectFileAsync(fileId);
            }

            if (queuedAny) workerSignal.Wake();
        }
        finally
        {
            DetectionLock.Release();
        }
    }

    public async Task<int?> GetNextFileIdAsync(CancellationToken ct = default)
    {
        return await unitOfWork.OcrRepository.GetNextFileIdToOcrAsync(ct);
    }

    public async Task<OcrPageResult> ProcessNextPageAsync(int mangaFileId, string sidecarUrl, CancellationToken ct = default)
    {
        var info = await unitOfWork.OcrRepository.GetOcrFileInfoAsync(mangaFileId, ct);
        if (info is not { OcrStatus: OcrStatus.Pending or OcrStatus.Processing })
            return new OcrPageResult(OcrPageOutcome.FileFinished);

        var file = (await unitOfWork.OcrRepository.GetOcrFileAsync(mangaFileId, ct))!;
        var sourceFile = directoryService.FileSystem.FileInfo.New(file.FilePath);
        if (!sourceFile.Exists)
        {
            // Keep the pages already read. Run OCR on the series once the file is back
            logger.LogWarning("[OcrService] {FilePath} is missing, so its OCR stops here", file.FilePath);
            await FinishFileAsync(file, info, OcrStatus.Failed, ct);
            return new OcrPageResult(OcrPageOutcome.FileFinished, info.SeriesId, info.ChapterId);
        }

        if (HasFileChangedSinceCheck(file)) return await ResetChangedFileAsync(file, info, ct);

        var page = await unitOfWork.OcrRepository.GetNextPendingPageAsync(mangaFileId, ct);
        if (page == null)
        {
            var counts = await unitOfWork.OcrRepository.GetPageStateCountsAsync(mangaFileId, ct);
            var allFailed = counts.GetValueOrDefault(OcrPageState.Failed) > 0 && counts.GetValueOrDefault(OcrPageState.Done) == 0;
            await FinishFileAsync(file, info, allFailed ? OcrStatus.Failed : OcrStatus.Done, ct);
            return new OcrPageResult(OcrPageOutcome.FileFinished, info.SeriesId, info.ChapterId);
        }

        if (file.OcrStatus == OcrStatus.Pending)
        {
            file.OcrStatus = OcrStatus.Processing;
            unitOfWork.MangaFileRepository.Update(file);
            await unitOfWork.CommitAsync(ct);
            await SendProgressAsync(info, page.PageNumber, ProgressEventType.Started, ct);
        }

        var pageNum = page.PageNumber + 1;
        byte[] image;
        try
        {
            image = info.Format == MangaFormat.Archive
                ? await ReadArchivePageAsync(info.ChapterId, page.PageNumber, ct)
                : bookService.RenderPdfPageForOcr(file.FilePath, page.PageNumber).Png;
        }
        catch (Exception ex)
        {
            // Rendering again won't go any differently
            logger.LogWarning(ex, "[OcrService] Couldn't render page {Page} of {FilePath}", pageNum, file.FilePath);
            page.Attempts = MaxPageAttempts;
            page.State = OcrPageState.Failed;
            await unitOfWork.CommitAsync(ct);
            return new OcrPageResult(OcrPageOutcome.PageFailed, info.SeriesId, info.ChapterId, pageNum);
        }

        OcrSidecarResponseDto response;
        try
        {
            response = await sidecarClient.RecognizeAsync(sidecarUrl, image, SidecarEngine(info), ct);
        }
        catch (OcrSidecarUnavailableException ex)
        {
            logger.LogWarning("[OcrService] {Message}", ex.Message);
            return new OcrPageResult(OcrPageOutcome.SidecarUnavailable, info.SeriesId, info.ChapterId, pageNum);
        }
        catch (OcrSidecarRejectedException ex)
        {
            page.Attempts++;
            if (page.Attempts >= MaxPageAttempts) page.State = OcrPageState.Failed;
            logger.LogWarning("[OcrService] Page {Page} of {FilePath}, attempt {Attempt}: {Message}",
                pageNum, file.FilePath, page.Attempts, ex.Message);
            await unitOfWork.CommitAsync(ct);
            return new OcrPageResult(OcrPageOutcome.PageFailed, info.SeriesId, info.ChapterId, pageNum);
        }

        // The page took seconds, and the file could have been replaced meanwhile
        if (HasFileChangedSinceCheck(file)) return await ResetChangedFileAsync(file, info, ct);

        page.Lines = ToLines(response);
        page.Engine = response.Engine;
        page.EngineVersion = response.EngineVersion;
        page.ImageWidth = response.ImageWidth;
        page.ImageHeight = response.ImageHeight;
        page.State = OcrPageState.Done;
        await unitOfWork.CommitAsync(ct);
        await SendProgressAsync(info, pageNum, ProgressEventType.Updated, ct);

        return new OcrPageResult(OcrPageOutcome.PageDone, info.SeriesId, info.ChapterId, pageNum);
    }

    public async Task<OcrChapterStatusDto?> GetChapterStatusAsync(int chapterId, CancellationToken ct = default)
    {
        var file = await unitOfWork.OcrRepository.GetOcrSourceFileForChapterAsync(chapterId, ct);
        if (file == null) return null;

        var counts = await unitOfWork.OcrRepository.GetPageStateCountsAsync(file.Id, ct);
        var donePages = await unitOfWork.OcrRepository.GetDonePageNumbersAsync(file.Id, ct);
        return new OcrChapterStatusDto
        {
            Status = file.OcrStatus,
            Pages = donePages.Select(p => p + 1).ToList(),
            PendingPages = counts.GetValueOrDefault(OcrPageState.Pending),
            FailedPages = counts.GetValueOrDefault(OcrPageState.Failed),
        };
    }

    public async Task<OcrPageDto?> GetPageAsync(int chapterId, int pageNum, CancellationToken ct = default)
    {
        var file = await unitOfWork.OcrRepository.GetOcrSourceFileForChapterAsync(chapterId, ct);
        if (file == null || pageNum < 1) return null;

        var page = await unitOfWork.OcrRepository.GetDonePageAsync(file.Id, pageNum - 1, ct);
        return page == null ? null : ToDto(page);
    }

    public async Task<IList<OcrPageDto>?> GetChapterPagesAsync(int chapterId, CancellationToken ct = default)
    {
        var file = await unitOfWork.OcrRepository.GetOcrSourceFileForChapterAsync(chapterId, ct);
        if (file == null) return null;

        var pages = await unitOfWork.OcrRepository.GetDonePagesAsync(file.Id, ct);
        return pages.Select(ToDto).ToList();
    }

    private static OcrPageDto ToDto(OcrPage page)
    {
        return new OcrPageDto
        {
            PageNumber = page.PageNumber + 1,
            Version = page.LastModifiedUtc.Ticks,
            Lines = page.Lines,
        };
    }

    public async Task PrioritizeChapterAsync(int chapterId, CancellationToken ct = default)
    {
        var chapterFile = await unitOfWork.OcrRepository.GetOcrSourceFileForChapterAsync(chapterId, ct);
        if (chapterFile == null) return;

        // Only files in OCR libraries are worth moving up
        var file = await unitOfWork.OcrRepository.GetOcrFileAsync(chapterFile.Id, ct);
        if (file is not { OcrStatus: OcrStatus.NotChecked or OcrStatus.Pending or OcrStatus.Processing }) return;
        if (file.OcrPriority >= ReaderPriority) return;

        file.OcrPriority = ReaderPriority;
        unitOfWork.MangaFileRepository.Update(file);
        await unitOfWork.CommitAsync(ct);

        if (file.OcrStatus == OcrStatus.NotChecked) QueueDetection();
        else workerSignal.Wake();
    }

    public async Task<int> RunSeriesAsync(int seriesId, bool replaceExistingText, CancellationToken ct = default)
    {
        var files = await unitOfWork.OcrRepository.GetOcrFilesForSeriesAsync(seriesId, ct: ct);
        var queued = 0;
        var needsCheck = false;

        foreach (var file in files)
        {
            if (replaceExistingText)
            {
                var sourceFile = directoryService.FileSystem.FileInfo.New(file.FilePath);
                if (!sourceFile.Exists || file.Pages <= 0) continue;

                await unitOfWork.OcrRepository.DeletePagesForFileAsync(file.Id, ct);
                for (var page = 0; page < file.Pages; page++)
                {
                    unitOfWork.OcrRepository.Add(new OcrPage { MangaFileId = file.Id, PageNumber = page, State = OcrPageState.Pending });
                }

                file.OcrStatus = OcrStatus.Pending;
                file.OcrSourceWriteTimeUtc = sourceFile.LastWriteTimeUtc;
            }
            else if (file.OcrStatus is OcrStatus.NotChecked or OcrStatus.Removed or OcrStatus.Failed)
            {
                await unitOfWork.OcrRepository.DeletePagesForFileAsync(file.Id, ct);
                file.OcrStatus = OcrStatus.NotChecked;
                needsCheck = true;
            }
            else if (file.OcrStatus is not (OcrStatus.Pending or OcrStatus.Processing))
            {
                // HasText or Done: there's nothing left to OCR without replacing text
                continue;
            }

            file.OcrPriority = AdminPriority;
            file.OcrRequestedUtc = DateTime.UtcNow;
            unitOfWork.MangaFileRepository.Update(file);
            queued++;
        }

        if (unitOfWork.HasChanges()) await unitOfWork.CommitAsync(ct);
        if (needsCheck) QueueDetection();
        if (queued > 0) workerSignal.Wake();
        return queued;
    }

    public async Task RemoveSeriesAsync(int seriesId, CancellationToken ct = default)
    {
        var files = await unitOfWork.OcrRepository.GetOcrFilesForSeriesAsync(seriesId, includeLibrariesWithoutOcr: true, ct);
        foreach (var file in files)
        {
            await unitOfWork.OcrRepository.DeletePagesForFileAsync(file.Id, ct);
            file.OcrStatus = OcrStatus.Removed;
            file.OcrPriority = 0;
            unitOfWork.MangaFileRepository.Update(file);
        }

        if (unitOfWork.HasChanges()) await unitOfWork.CommitAsync(ct);
    }

    /// <returns>Whether any page was queued for OCR</returns>
    private async Task<bool> DetectFileAsync(int mangaFileId)
    {
        var file = await unitOfWork.OcrRepository.GetOcrFileAsync(mangaFileId);
        if (file is not { OcrStatus: OcrStatus.NotChecked }) return false;

        var sourceFile = directoryService.FileSystem.FileInfo.New(file.FilePath);
        if (!sourceFile.Exists) return false;

        int[] textLengths;
        try
        {
            // An archive's pages are images with no text of their own
            textLengths = file.Format == MangaFormat.Archive
                ? new int[file.Pages]
                : bookService.GetPdfPageTextLengths(file.FilePath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[OcrService] Couldn't read the text of {FilePath}, so it won't be OCR'd", file.FilePath);
            file.OcrStatus = OcrStatus.Failed;
            file.OcrSourceWriteTimeUtc = sourceFile.LastWriteTimeUtc;
            unitOfWork.MangaFileRepository.Update(file);
            await unitOfWork.CommitAsync();
            return false;
        }

        await unitOfWork.OcrRepository.DeletePagesForFileAsync(file.Id);

        var scannedPages = Enumerable.Range(0, textLengths.Length)
            .Where(page => textLengths[page] < MinCharactersForText)
            .ToList();
        foreach (var page in scannedPages)
        {
            unitOfWork.OcrRepository.Add(new OcrPage { MangaFileId = file.Id, PageNumber = page, State = OcrPageState.Pending });
        }

        file.OcrStatus = scannedPages.Count == 0 ? OcrStatus.HasText : OcrStatus.Pending;
        if (scannedPages.Count > 0) file.OcrRequestedUtc ??= DateTime.UtcNow;
        file.OcrSourceWriteTimeUtc = sourceFile.LastWriteTimeUtc;
        unitOfWork.MangaFileRepository.Update(file);
        await unitOfWork.CommitAsync();

        logger.LogInformation("[OcrService] {FilePath} has {ScannedPages} of {Pages} pages without text",
            file.FilePath, scannedPages.Count, textLengths.Length);
        return scannedPages.Count > 0;
    }

    private async Task<OcrPageResult> ResetChangedFileAsync(MangaFile file, OcrFileInfoDto info, CancellationToken ct)
    {
        logger.LogInformation("[OcrService] {FilePath} changed during OCR, so it will be checked again", file.FilePath);
        await unitOfWork.OcrRepository.DeletePagesForFileAsync(file.Id, ct);
        file.OcrStatus = OcrStatus.NotChecked;
        unitOfWork.MangaFileRepository.Update(file);
        await unitOfWork.CommitAsync(ct);
        await SendProgressAsync(info, 0, ProgressEventType.Ended, ct);
        QueueDetection();
        return new OcrPageResult(OcrPageOutcome.FileChanged, info.SeriesId, info.ChapterId);
    }

    private async Task FinishFileAsync(MangaFile file, OcrFileInfoDto info, OcrStatus status, CancellationToken ct)
    {
        file.OcrStatus = status;
        file.OcrPriority = 0;
        unitOfWork.MangaFileRepository.Update(file);
        await unitOfWork.CommitAsync(ct);
        await SendProgressAsync(info, 0, ProgressEventType.Ended, ct);
    }

    private async Task SendProgressAsync(OcrFileInfoDto info, int pageNum, string eventType, CancellationToken ct)
    {
        var counts = await unitOfWork.OcrRepository.GetPageStateCountsAsync(info.MangaFileId, ct);
        var total = counts.Values.Sum();
        var finished = total - counts.GetValueOrDefault(OcrPageState.Pending);
        var progress = eventType == ProgressEventType.Ended || total == 0 ? 1f : finished / (float) total;
        var subtitle = pageNum > 0 ? $"{info.SeriesName}: page {pageNum}, {finished} of {total} done" : info.SeriesName;

        await eventHub.SendMessageAsync(MessageFactory.NotificationProgress,
            MessageFactory.OcrProgressEvent(info.LibraryId, progress, eventType, subtitle), ct: ct);
    }

    private static List<OcrLine> ToLines(OcrSidecarResponseDto response)
    {
        float width = Math.Max(response.ImageWidth, 1);
        float height = Math.Max(response.ImageHeight, 1);

        return response.Lines
            .Where(l => l.Box.Length == 4 && !string.IsNullOrWhiteSpace(l.Text))
            .OrderBy(l => l.Order)
            .Select(l => new OcrLine
            {
                Order = l.Order,
                Text = l.Text,
                X = l.Box[0] / width,
                Y = l.Box[1] / height,
                Width = l.Box[2] / width,
                Height = l.Box[3] / height,
                Vertical = l.Vertical,
                Kind = ToKind(l.Kind),
                DetectionConfidence = l.DetConf,
                RecognitionConfidence = l.RecConf,
                Block = l.Block,
            })
            .ToList();
    }

    private static OcrLineKind ToKind(string kind) => kind switch
    {
        "title" => OcrLineKind.Title,
        "caption" => OcrLineKind.Caption,
        "ruby" => OcrLineKind.Ruby,
        "header" => OcrLineKind.Header,
        "folio" => OcrLineKind.Folio,
        _ => OcrLineKind.Body,
    };

    /// <summary>
    /// The sidecar's name for the engine that reads this file's library
    /// </summary>
    public static string SidecarEngine(OcrFileInfoDto info)
    {
        var engine = info.OcrEngine;
        if (engine == OcrEngine.Auto)
        {
            engine = info.LibraryType is LibraryType.Manga or LibraryType.Comic or LibraryType.ComicVine
                ? OcrEngine.Manga
                : OcrEngine.Books;
        }

        return engine == OcrEngine.Manga ? "manga" : "ndlocr";
    }

    /// <summary>
    /// Reads a page from the chapter's cache folder, the one the reader serves pages from, so OCR sees the same image
    /// at the same page number. The sidecar decodes JPEG, PNG and WebP, and anything else goes as PNG
    /// </summary>
    private async Task<byte[]> ReadArchivePageAsync(int chapterId, int pageNumber, CancellationToken ct)
    {
        if (await cacheService.Ensure(chapterId, ct: ct) == null)
            throw new KavitaException($"Chapter {chapterId} couldn't be extracted to the cache");

        var path = cacheService.GetCachedPagePath(chapterId, pageNumber);
        if (string.IsNullOrEmpty(path)) throw new KavitaException($"Chapter {chapterId} has no page {pageNumber + 1}");

        var extension = directoryService.FileSystem.Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".jpg" or ".jpeg" or ".png" or ".webp")
            return await directoryService.FileSystem.File.ReadAllBytesAsync(path, ct);

        using var image = NetVips.Image.NewFromFile(path, access: NetVips.Enums.Access.Sequential);
        return image.WriteToBuffer(".png");
    }

    /// <remarks>A missing file counts as unchanged, so a file on an unmounted drive keeps its OCR</remarks>
    private bool HasFileChangedSinceCheck(MangaFile file)
    {
        var sourceFile = directoryService.FileSystem.FileInfo.New(file.FilePath);
        if (!sourceFile.Exists) return false;
        return file.OcrSourceWriteTimeUtc == null ||
               !FolderChangeCheck.IsSameWriteTime(file.OcrSourceWriteTimeUtc.Value, sourceFile.LastWriteTimeUtc);
    }

    private static void QueueDetection()
    {
        if (Kavita.Services.TaskScheduler.HasAlreadyEnqueuedTask(nameof(IOcrService), nameof(IOcrService.DetectNewFilesAsync), [])) return;
        BackgroundJob.Enqueue<IOcrService>(service => service.DetectNewFilesAsync());
    }
}
