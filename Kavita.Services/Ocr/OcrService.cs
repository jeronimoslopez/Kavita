using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Kavita.API.Database;
using Kavita.API.Services;
using Kavita.Models.Entities;
using Kavita.Models.Entities.Enums;
using Kavita.Services.Scanner;
using Microsoft.Extensions.Logging;

namespace Kavita.Services.Ocr;

public class OcrService(ILogger<OcrService> logger, IUnitOfWork unitOfWork, IBookService bookService,
    IDirectoryService directoryService) : IOcrService
{
    /// <summary>
    /// A page with fewer non-whitespace characters than this is treated as a scan. Scans often carry a stray page number
    /// or a single glyph from the scanner's software
    /// </summary>
    public const int MinCharactersForText = 3;

    public async Task MarkSeriesForCheckAsync(int seriesId, CancellationToken ct = default)
    {
        var files = await unitOfWork.OcrRepository.GetOcrFilesForSeriesAsync(seriesId, ct);
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
        var fileIds = await unitOfWork.OcrRepository.GetFileIdsNeedingCheckAsync();
        foreach (var fileId in fileIds)
        {
            await DetectFileAsync(fileId);
        }
    }

    private async Task DetectFileAsync(int mangaFileId)
    {
        var file = await unitOfWork.OcrRepository.GetOcrFileAsync(mangaFileId);
        if (file is not { OcrStatus: OcrStatus.NotChecked }) return;

        var sourceFile = directoryService.FileSystem.FileInfo.New(file.FilePath);
        if (!sourceFile.Exists) return;

        int[] textLengths;
        try
        {
            textLengths = bookService.GetPdfPageTextLengths(file.FilePath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[OcrService] Couldn't read the text of {FilePath}, so it won't be OCR'd", file.FilePath);
            file.OcrStatus = OcrStatus.Failed;
            file.OcrSourceWriteTimeUtc = sourceFile.LastWriteTimeUtc;
            unitOfWork.MangaFileRepository.Update(file);
            await unitOfWork.CommitAsync();
            return;
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
