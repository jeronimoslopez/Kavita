using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Hangfire;
using Kavita.API.Database;
using Kavita.API.Services;
using Kavita.API.Services.Reading;
using Kavita.Common.Extensions;
using Kavita.Models.DTOs.Dictionary;
using Kavita.Models.Entities.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VersOne.Epub;

namespace Kavita.Services.Dictionary;

/// <summary>
/// What the tokenizer needs from an EPUB, numbered the way the book reader numbers its pages
/// </summary>
public interface IEpubPageSource
{
    /// <returns>The number of pages the book reader counts for the file</returns>
    Task<int> GetEpubPageCountAsync(string epubFilePath, CancellationToken ct = default);
    /// <param name="page">0-based page</param>
    /// <returns>The page's blocks, or null when the page doesn't exist or isn't XHTML</returns>
    Task<IReadOnlyList<EpubTextBlock>?> GetEpubPageBlocksAsync(string epubFilePath, int page, CancellationToken ct = default);
}

public class BookServiceEpubPageSource(IBookService bookService) : IEpubPageSource
{
    public Task<int> GetEpubPageCountAsync(string epubFilePath, CancellationToken ct = default) =>
        bookService.GetEpubPageCountAsync(epubFilePath, ct);
    public Task<IReadOnlyList<EpubTextBlock>?> GetEpubPageBlocksAsync(string epubFilePath, int page, CancellationToken ct = default) =>
        bookService.GetEpubPageBlocksAsync(epubFilePath, page, ct);
}

public class TokenizeService : ITokenizeService
{
    /// <summary>
    /// Times a page may fail before its unit is marked failed
    /// </summary>
    public const int MaxUnitAttempts = 3;

    /// <summary>
    /// The scanner, the worker, a queued Hangfire job and a reader opening a chapter can all start a check. Two checks
    /// of one file would open the EPUB twice and seed it twice
    /// </summary>
    private static readonly SemaphoreSlim DetectionLock = new(1, 1);
    /// <summary>
    /// Paths of files whose pages couldn't be counted, or that have none, with the stamp they had. They have no units, so
    /// without this every detection would open them again
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> UnusableFiles = new();

    private readonly ILogger<TokenizeService> _logger;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IDirectoryService _directoryService;
    private readonly IReadingStore _readingStore;
    private readonly ITextSidecarClient _sidecarClient;
    private readonly ITokenizeWorkerSignal _workerSignal;
    private readonly IReaderService _readerService;
    private readonly IEpubPageSource _pageSource;

    public TokenizeService(ILogger<TokenizeService> logger, IUnitOfWork unitOfWork, IBookService bookService,
        IDirectoryService directoryService, IReadingStore readingStore, ITextSidecarClient sidecarClient,
        ITokenizeWorkerSignal workerSignal, IReaderService readerService)
        : this(logger, unitOfWork, new BookServiceEpubPageSource(bookService), directoryService, readingStore,
            sidecarClient, workerSignal, readerService)
    {
    }

    internal TokenizeService(ILogger<TokenizeService> logger, IUnitOfWork unitOfWork, IEpubPageSource pageSource,
        IDirectoryService directoryService, IReadingStore readingStore, ITextSidecarClient sidecarClient,
        ITokenizeWorkerSignal workerSignal, IReaderService readerService)
    {
        _logger = logger;
        _unitOfWork = unitOfWork;
        _pageSource = pageSource;
        _directoryService = directoryService;
        _readingStore = readingStore;
        _sidecarClient = sidecarClient;
        _workerSignal = workerSignal;
        _readerService = readerService;
    }

    public async Task MarkSeriesForCheckAsync(int seriesId, CancellationToken ct = default)
    {
        // The scanner calls this for every series, so it only compares stamps and leaves opening EPUBs and deleting
        // rows to the detection job it queues
        var settings = await _unitOfWork.SettingsRepository.GetSettingsDtoAsync(ct);
        if (string.IsNullOrWhiteSpace(settings.TextSidecarUrl)) return;

        var eligible = (await GetFilesAsync(f => f.Chapter.Volume.SeriesId == seriesId &&
                                                 f.Chapter.Volume.Series.Library.EnableDictionary, ct))
            .Where(IsEligible)
            .ToList();
        if (eligible.Count == 0) return;

        var stamps = await _readingStore.GetFileStampsAsync(ct);
        if (eligible.Any(file => NeedsSeeding(file, stamps) != null)) QueueDetection();
    }

    [AutomaticRetry(Attempts = 0)]
    public async Task DetectNewFilesAsync()
    {
        // Whoever holds the lock will reach these files, or the worker's next pass will
        if (!await DetectionLock.WaitAsync(0)) return;
        try
        {
            var stamps = await _readingStore.GetFileStampsAsync();
            var eligible = (await GetFilesAsync(f => f.Chapter.Volume.Series.Library.EnableDictionary, CancellationToken.None))
                .Where(IsEligible)
                .ToList();
            var queued = await SeedChangedFilesAsync(eligible, stamps, ReadingPriority.WholeBook, CancellationToken.None);

            // Files deleted from Kavita, and files in libraries that turned the dictionary off, have no row here
            var eligibleIds = eligible.Select(f => f.Id).ToHashSet();
            var removed = stamps.Keys.Where(id => !eligibleIds.Contains(id)).ToList();
            if (removed.Count > 0) await _readingStore.DeleteFilesAsync(removed);

            if (queued) _workerSignal.Wake();
        }
        finally
        {
            DetectionLock.Release();
        }
    }

    public async Task<ReadingUnitRef?> GetNextUnitAsync(CancellationToken ct = default)
    {
        return await _readingStore.GetNextPendingUnitAsync(ct);
    }

    public async Task<TokenizeUnitResult> ProcessUnitAsync(ReadingUnitRef unit, string sidecarUrl, CancellationToken ct = default)
    {
        var file = (await GetFilesAsync(f => f.Id == unit.MangaFileId, ct)).FirstOrDefault();
        if (file == null || !IsEligible(file))
        {
            await _readingStore.DeleteFilesAsync([unit.MangaFileId], ct);
            return new TokenizeUnitResult(TokenizeUnitOutcome.FileChanged, file?.SeriesId ?? 0, file?.ChapterId ?? 0, unit.Page);
        }

        if (await CheckFileAsync(file, unit, ct) is { } before)
            return await EndBeforeSavingAsync(before, file, unit, ct);

        try
        {
            var pageBlocks = await _pageSource.GetEpubPageBlocksAsync(file.FilePath, unit.Page, ct);
            var blocks = (pageBlocks ?? []).OrderBy(b => b.Index).ToList();

            List<StoredBlock> stored;
            List<AnalysisSpan> ambiguous;
            var tokenizerVersion = string.Empty;
            if (blocks.Count == 0)
            {
                stored = [];
                ambiguous = [];
            }
            else
            {
                var request = blocks.Select(b => new TokenizeBlockDto(b.Index.ToString(CultureInfo.InvariantCulture), b.Text)).ToList();
                var response = await _sidecarClient.TokenizeAsync(sidecarUrl, request, ct);
                Validate(blocks, response);

                // Tokenizing took a moment, and the file could have been replaced meanwhile
                if (await CheckFileAsync(file, unit, ct) is { } after)
                    return await EndBeforeSavingAsync(after, file, unit, ct);

                var wordIds = await _readingStore.UpsertWordsAsync(response.Words.ToList(), LexiconVersion(response.Version), ct);
                (stored, ambiguous) = ToStored(blocks, response, wordIds);
                tokenizerVersion = response.Version;
            }

            var saved = await _readingStore.SaveTokenizedUnitAsync(unit.Id, unit.SourceStamp, tokenizerVersion, stored, ambiguous, ct);
            return new TokenizeUnitResult(saved ? TokenizeUnitOutcome.UnitDone : TokenizeUnitOutcome.FileChanged,
                file.SeriesId, file.ChapterId, unit.Page);
        }
        catch (TextSidecarUnavailableException ex)
        {
            _logger.LogWarning("[TokenizeService] {Message}", ex.Message);
            return new TokenizeUnitResult(TokenizeUnitOutcome.SidecarUnavailable, file.SeriesId, file.ChapterId, unit.Page);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var state = await _readingStore.RecordFailedAttemptAsync(unit.Id, MaxUnitAttempts, ct);
            _logger.LogWarning(ex, "[TokenizeService] Page {Page} of {FilePath}, attempt {Attempt}, is now {State}: {Message}",
                unit.Page + 1, file.FilePath, unit.Attempts + 1, state, ex.Message);
            return new TokenizeUnitResult(TokenizeUnitOutcome.UnitFailed, file.SeriesId, file.ChapterId, unit.Page);
        }
    }

    public async Task<PrioritizedFiles> PrioritizeChapterAsync(int chapterId, CancellationToken ct = default)
    {
        var chapter = await _unitOfWork.DataContext.Chapter
            .Where(c => c.Id == chapterId)
            .Select(c => new { c.VolumeId, c.Volume.SeriesId })
            .FirstOrDefaultAsync(ct);
        if (chapter == null) return new PrioritizedFiles([], []);

        // The user only matters to the reader for progress, which doesn't change the order
        var nextChapterId = await _readerService.GetNextChapterIdAsync(chapter.SeriesId, chapter.VolumeId, chapterId, 0, ct);

        var openFiles = (await GetFilesAsync(f => f.ChapterId == chapterId, ct)).Where(IsEligible).ToList();
        var nextFiles = nextChapterId > 0
            ? (await GetFilesAsync(f => f.ChapterId == nextChapterId, ct)).Where(IsEligible).ToList()
            : [];

        // A file the scanner hasn't reached yet gets its units now, so the reader isn't left waiting for the next check.
        // While another check runs the file is left to it, and raising its priority below finds no units
        if (await DetectionLock.WaitAsync(0, ct))
        {
            try
            {
                var stamps = await _readingStore.GetFileStampsAsync(ct);
                await SeedChangedFilesAsync(openFiles, stamps, ReadingPriority.OpenChapter, ct);
                await SeedChangedFilesAsync(nextFiles, stamps, ReadingPriority.NextChapter, ct);
            }
            finally
            {
                DetectionLock.Release();
            }
        }

        foreach (var file in openFiles) await _readingStore.RaiseUnitPriorityAsync(file.Id, null, ReadingPriority.OpenChapter, ct);
        foreach (var file in nextFiles) await _readingStore.RaiseUnitPriorityAsync(file.Id, null, ReadingPriority.NextChapter, ct);

        if (openFiles.Count > 0 || nextFiles.Count > 0) _workerSignal.Wake();
        return new PrioritizedFiles(openFiles.Select(f => f.Id).ToList(), nextFiles.Select(f => f.Id).ToList());
    }

    /// <summary>
    /// The lexicon part of the sidecar's version string. The lexicon's own version has semicolons in it, so it runs
    /// from <c>lexicon=</c> up to <c>;sidecar=</c> and not to the next semicolon
    /// </summary>
    /// <returns>The empty string when the version has no lexicon part</returns>
    public static string LexiconVersion(string version)
    {
        const string marker = "lexicon=";
        var start = version.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return string.Empty;

        start += marker.Length;
        var end = version.IndexOf(";sidecar=", start, StringComparison.Ordinal);
        return end < 0 ? version[start..] : version[start..end];
    }

    /// <summary>
    /// Lets tests that reuse file ids and paths start from an empty <see cref="UnusableFiles"/>
    /// </summary>
    internal static void ForgetUnusableFiles() => UnusableFiles.Clear();

    /// <summary>
    /// Japanese as an ISO 639 code, alone or with a region or script, such as <c>ja</c>, <c>ja-JP</c> or <c>jpn</c>
    /// </summary>
    public static bool IsJapanese(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return false;
        var code = language.Trim().ToLowerInvariant();
        return code is "ja" or "jpn" || code.StartsWith("ja-") || code.StartsWith("ja_");
    }

    private static bool IsEligible(EpubFile file)
    {
        var language = string.IsNullOrWhiteSpace(file.ChapterLanguage) ? file.SeriesLanguage : file.ChapterLanguage;
        return file.EnableDictionary && IsJapanese(language);
    }

    private async Task<List<EpubFile>> GetFilesAsync(System.Linq.Expressions.Expression<Func<Kavita.Models.Entities.MangaFile, bool>> filter,
        CancellationToken ct)
    {
        return await _unitOfWork.DataContext.MangaFile
            .Where(f => f.Format == MangaFormat.Epub)
            .Where(filter)
            .Select(f => new EpubFile(f.Id, f.FilePath, f.ChapterId, f.Chapter.Volume.SeriesId,
                f.Chapter.Volume.Series.Library.EnableDictionary, f.Chapter.Language, f.Chapter.Volume.Series.Metadata.Language))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Seeds the files that have no units yet or whose stamp changed since they were seeded
    /// </summary>
    /// <returns>Whether any file was seeded</returns>
    private async Task<bool> SeedChangedFilesAsync(IEnumerable<EpubFile> files, IReadOnlyDictionary<int, string> stamps,
        int priority, CancellationToken ct)
    {
        var seeded = false;
        foreach (var file in files)
        {
            if (NeedsSeeding(file, stamps) is not { } stamp) continue;
            seeded |= await SeedFileAsync(file, stamp, priority, ct) == SeedResult.Seeded;
        }

        return seeded;
    }

    /// <returns>The file's current stamp when it has no units or its units have another stamp, and null otherwise</returns>
    private string? NeedsSeeding(EpubFile file, IReadOnlyDictionary<int, string> stamps)
    {
        // A file on an unmounted drive keeps its units
        var stamp = GetStamp(file.FilePath);
        if (stamp == null) return null;
        if (stamps.TryGetValue(file.Id, out var stored) && stored == stamp) return null;
        if (UnusableFiles.TryGetValue(file.FilePath, out var unusable) && unusable == stamp) return null;
        return stamp;
    }

    private enum SeedResult
    {
        Seeded,
        /// <summary>
        /// The file isn't a readable EPUB or has no pages, so it has no units now
        /// </summary>
        NoPages,
        /// <summary>
        /// The file couldn't be read this time, and its units were left as they were
        /// </summary>
        Unavailable,
    }

    private async Task<SeedResult> SeedFileAsync(EpubFile file, string stamp, int priority, CancellationToken ct)
    {
        int pageCount;
        try
        {
            pageCount = await _pageSource.GetEpubPageCountAsync(file.FilePath, ct);
        }
        catch (Exception ex) when (ex is EpubReaderException or InvalidDataException or XmlException)
        {
            // Units left from before the file broke would hold text it no longer has
            _logger.LogWarning(ex, "[TokenizeService] {FilePath} isn't a readable EPUB, so it won't be tokenized", file.FilePath);
            UnusableFiles[file.FilePath] = stamp;
            await _readingStore.DeleteFilesAsync([file.Id], ct);
            return SeedResult.NoPages;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A locked file or a dropped network share says nothing about the book, so its answers stay
            _logger.LogWarning(ex, "[TokenizeService] Couldn't read {FilePath} this time: {Message}", file.FilePath, ex.Message);
            return SeedResult.Unavailable;
        }

        if (pageCount <= 0) UnusableFiles[file.FilePath] = stamp;
        else UnusableFiles.TryRemove(file.FilePath, out _);

        await _readingStore.SeedFileAsync(file.Id, Math.Max(pageCount, 0), stamp, priority, ct);
        _logger.LogInformation("[TokenizeService] Queued {Pages} pages of {FilePath}", pageCount, file.FilePath);
        return pageCount > 0 ? SeedResult.Seeded : SeedResult.NoPages;
    }

    /// <summary>
    /// Compares the file on disk with the stamp the unit was seeded with, and seeds the file again when they differ
    /// </summary>
    /// <returns>Null when the unit can be tokenized, or the outcome that ends this attempt</returns>
    private async Task<TokenizeUnitOutcome?> CheckFileAsync(EpubFile file, ReadingUnitRef unit, CancellationToken ct)
    {
        var stamp = GetStamp(file.FilePath);
        if (stamp == null)
        {
            _logger.LogWarning("[TokenizeService] {FilePath} isn't on disk, so tokenizing waits for it", file.FilePath);
            return TokenizeUnitOutcome.FileUnavailable;
        }

        if (stamp == unit.SourceStamp) return null;

        _logger.LogInformation("[TokenizeService] {FilePath} changed, so its pages will be tokenized again", file.FilePath);
        return await SeedFileAsync(file, stamp, ReadingPriority.WholeBook, ct) == SeedResult.Unavailable
            ? TokenizeUnitOutcome.FileUnavailable
            : TokenizeUnitOutcome.FileChanged;
    }

    private async Task<TokenizeUnitResult> EndBeforeSavingAsync(TokenizeUnitOutcome outcome, EpubFile file,
        ReadingUnitRef unit, CancellationToken ct)
    {
        // The unit is still pending, so without this it would be picked first again and hold up every unit behind it
        if (outcome == TokenizeUnitOutcome.FileUnavailable) await _readingStore.DeferUnitAsync(unit.Id, ct);
        return new TokenizeUnitResult(outcome, file.SeriesId, file.ChapterId, unit.Page);
    }

    /// <summary>
    /// The file's last write time in UTC ticks, cut to the second as <c>FolderChangeCheck.IsSameWriteTime</c> compares
    /// times, because some file systems keep only whole seconds
    /// </summary>
    /// <returns>Null when the file isn't on disk</returns>
    private string? GetStamp(string filePath)
    {
        var info = _directoryService.FileSystem.FileInfo.New(filePath);
        if (!info.Exists) return null;
        return info.LastWriteTimeUtc.Truncate(TimeSpan.TicksPerSecond).Ticks.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Checks the answer before anything is stored, so a sidecar bug fails the page instead of saving offsets the reader
    /// would misplace
    /// </summary>
    private static void Validate(IReadOnlyList<EpubTextBlock> blocks, TokenizeResponseDto response)
    {
        if (response.Blocks.Count != blocks.Count)
            throw new InvalidDataException($"The sidecar answered {response.Blocks.Count} blocks for {blocks.Count}");

        var wordCount = response.Words.Count;
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var answer = response.Blocks[i];
            if (answer.Id != block.Index.ToString(CultureInfo.InvariantCulture))
                throw new InvalidDataException($"The sidecar answered block {answer.Id} where block {block.Index} was sent");

            var end = 0;
            foreach (var token in answer.Tokens)
            {
                CheckSpan(block, token.S, token.E, token.W, end, block.Text.Length, wordCount);
                end = token.E;

                var partEnd = token.S;
                foreach (var part in token.Sub ?? [])
                {
                    CheckSpan(block, part.S, part.E, part.W, partEnd, token.E, wordCount);
                    partEnd = part.E;
                }
            }
        }
    }

    /// <param name="min">The earliest the span may start, which is where the one before it ended</param>
    /// <param name="max">The latest the span may end</param>
    private static void CheckSpan(EpubTextBlock block, int s, int e, long? w, int min, int max, int wordCount)
    {
        if (s < min || e <= s || e > max)
            throw new InvalidDataException($"Token [{s}, {e}] in block {block.Index} is out of order or outside its text");
        if (w is { } index && (index < 0 || index >= wordCount))
            throw new InvalidDataException($"Token [{s}, {e}] in block {block.Index} points at word {index} of {wordCount}");
    }

    private static (List<StoredBlock>, List<AnalysisSpan>) ToStored(IReadOnlyList<EpubTextBlock> blocks,
        TokenizeResponseDto response, IReadOnlyList<long> wordIds)
    {
        var stored = new List<StoredBlock>(blocks.Count);
        var ambiguous = new List<AnalysisSpan>();

        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var tokens = new List<TokenDto>(response.Blocks[i].Tokens.Count);
            foreach (var token in response.Blocks[i].Tokens)
            {
                long? wordId = token.W is { } w ? wordIds[(int) w] : null;
                tokens.Add(token with
                {
                    W = wordId,
                    Sub = token.Sub?.Select(part => part with { W = part.W is { } pw ? wordIds[(int) pw] : null }).ToList(),
                });

                // Parts of a compound never get an analysis row
                if (wordId != null && response.Words[(int) token.W!.Value].IsAmbiguous)
                    ambiguous.Add(new AnalysisSpan(block.Index, token.S, token.E, wordId.Value));
            }

            stored.Add(new StoredBlock(block.Index, block.Text, tokens));
        }

        return (stored, ambiguous);
    }

    /// <summary>
    /// Queues <see cref="DetectNewFilesAsync"/> unless a run is already queued
    /// </summary>
    internal static void QueueDetection()
    {
        if (Kavita.Services.TaskScheduler.HasAlreadyEnqueuedTask(nameof(ITokenizeService), nameof(ITokenizeService.DetectNewFilesAsync), [])) return;
        BackgroundJob.Enqueue<ITokenizeService>(service => service.DetectNewFilesAsync());
    }

    private sealed record EpubFile(int Id, string FilePath, int ChapterId, int SeriesId, bool EnableDictionary,
        string? ChapterLanguage, string? SeriesLanguage);
}
