using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kavita.Models.DTOs.Dictionary;

namespace Kavita.API.Services;

/// <summary>
/// Keeps reading.db's units in step with the Japanese EPUBs of dictionary libraries, and tokenizes them one page at a time
/// </summary>
/// <remarks>
/// A file is eligible when its library has <c>EnableDictionary</c> on, it's an EPUB, and its chapter's language is
/// Japanese. Its source stamp is its last write time, the way <c>OcrService</c> tells a changed file.
/// </remarks>
public interface ITokenizeService
{
    /// <summary>
    /// Seeds units for the series' eligible EPUBs that are new or changed since they were last seeded, deletes the units
    /// of its files that are no longer eligible, and wakes the worker when anything was queued
    /// </summary>
    /// <remarks>Compares write times only, so the scanner can call it for every series</remarks>
    Task MarkSeriesForCheckAsync(int seriesId, CancellationToken ct = default);
    /// <summary>
    /// Does what <see cref="MarkSeriesForCheckAsync"/> does for every series in dictionary libraries
    /// </summary>
    /// <remarks>Takes no arguments so a queued Hangfire run can be found and not queued twice</remarks>
    Task DetectNewFilesAsync();

    /// <returns>The unit the worker should tokenize next, or null when nothing is waiting</returns>
    Task<ReadingUnitRef?> GetNextUnitAsync(CancellationToken ct = default);
    /// <summary>
    /// Reads the unit's page, tokenizes its blocks with the sidecar and saves the blocks, words and analysis rows
    /// </summary>
    /// <param name="sidecarUrl">The current <c>TextSidecarUrl</c>, never empty</param>
    Task<TokenizeUnitResult> ProcessUnitAsync(ReadingUnitRef unit, string sidecarUrl, CancellationToken ct = default);

    /// <summary>
    /// Moves the chapter's EPUB pages ahead in the tokenization queue at <see cref="ReadingPriority.OpenChapter"/>, and the
    /// next chapter's at <see cref="ReadingPriority.NextChapter"/>, then wakes the worker
    /// </summary>
    /// <returns>The files it raised, so the caller can queue their analysis at the same priorities</returns>
    Task<PrioritizedFiles> PrioritizeChapterAsync(int chapterId, CancellationToken ct = default);
}

public enum TokenizeUnitOutcome
{
    /// <summary>
    /// The page was tokenized and saved
    /// </summary>
    UnitDone,
    /// <summary>
    /// The page couldn't be read or the sidecar refused it. The attempt was counted
    /// </summary>
    UnitFailed,
    /// <summary>
    /// The file changed on disk or is no longer eligible. Its units were seeded again or deleted, and nothing was saved
    /// </summary>
    FileChanged,
    /// <summary>
    /// The sidecar didn't answer. Nothing was changed
    /// </summary>
    SidecarUnavailable,
}

/// <param name="Page">0-based page the outcome is about</param>
public sealed record TokenizeUnitResult(TokenizeUnitOutcome Outcome, int SeriesId = 0, int ChapterId = 0, int Page = 0);

/// <param name="OpenChapterFileIds">Manga file ids of the chapter that was opened</param>
/// <param name="NextChapterFileIds">Manga file ids of the chapter after it in reading order. Empty at the last chapter</param>
public sealed record PrioritizedFiles(IReadOnlyList<int> OpenChapterFileIds, IReadOnlyList<int> NextChapterFileIds);

/// <summary>
/// Wakes the tokenize worker when there's new work, so it doesn't wait for its next poll
/// </summary>
public interface ITokenizeWorkerSignal
{
    void Wake();
}
