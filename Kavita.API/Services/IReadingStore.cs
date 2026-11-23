using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kavita.Models.DTOs.Dictionary;

namespace Kavita.API.Services;

/// <summary>
/// The tokenized text, word list and AI analysis in config/reading/reading.db. It's a separate SQLite file and not part
/// of <see cref="Kavita.API.Database.IUnitOfWork"/>, so every method commits on its own.
/// </summary>
/// <remarks>
/// A unit is one page of one file, keyed on (kind, manga file id, page). Every method here works on EPUB units, and
/// pages are 0-based as the book reader counts them. Ids from reading.db are longs. Queues order by priority, highest
/// first, then by the time the row was queued or last raised, oldest first, then by id.
/// </remarks>
public interface IReadingStore
{
    /// <summary>
    /// Inserts the words that are new and updates the rest in place, keyed on (lemma, pos, isProper)
    /// </summary>
    /// <param name="lexiconVersion">The <c>lexicon=</c> part of the sidecar's version, stored on every word written</param>
    /// <returns>The word id of each input word, in input order, so a sidecar word index maps to <c>ids[index]</c></returns>
    Task<IReadOnlyList<long>> UpsertWordsAsync(IReadOnlyList<SidecarWordDto> words, string lexiconVersion,
        CancellationToken ct = default);
    /// <returns>The words found. Ids that don't exist are left out</returns>
    Task<IReadOnlyDictionary<long, StoredWord>> GetWordsAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default);

    /// <summary>
    /// The stamp each file was last seeded with, so the tokenizer can tell new and changed files from the rest
    /// </summary>
    /// <returns>Manga file id to source stamp, for every file that has units</returns>
    Task<IReadOnlyDictionary<int, string>> GetFileStampsAsync(CancellationToken ct = default);
    /// <summary>
    /// Makes the file's units match its current pages. Pages from 0 to <paramref name="pageCount"/> - 1 without a unit
    /// get a pending one, and units past the end are deleted with their analysis. A unit with a different stamp goes
    /// back to pending with the new stamp and keeps its blocks and analysis until it's tokenized again. A failed unit
    /// with the same stamp stays failed
    /// </summary>
    /// <param name="sourceStamp">Changes whenever the file's content does, such as its last write time in UTC ticks</param>
    /// <param name="priority">Priority of the units this call creates or sends back to pending</param>
    Task SeedFileAsync(int mangaFileId, int pageCount, string sourceStamp, int priority, CancellationToken ct = default);
    /// <summary>
    /// Deletes the files' units, analysis rows and analysis requests. Words stay, because other files share them
    /// </summary>
    Task DeleteFilesAsync(IReadOnlyCollection<int> mangaFileIds, CancellationToken ct = default);

    /// <returns>The pending unit to tokenize next in queue order, or null when nothing is pending</returns>
    Task<ReadingUnitRef?> GetNextPendingUnitAsync(CancellationToken ct = default);
    /// <summary>
    /// Stores a tokenized unit and marks it done, and replaces its analysis rows, in one transaction. A row whose
    /// (block, s, e, word id) is in <paramref name="ambiguous"/> keeps its state, priority and answer. Other rows are
    /// deleted. A span that has no row yet gets one, pending at the file's analysis request priority when the file has
    /// a request and idle otherwise
    /// </summary>
    /// <param name="sourceStamp">The stamp from the <see cref="ReadingUnitRef"/> that was tokenized</param>
    /// <param name="blocks">Every block of the page in index order, with word ids in its tokens</param>
    /// <param name="ambiguous">The top-level tokens whose word <see cref="SidecarWordDto.IsAmbiguous"/>. Parts of
    /// compounds never get a row</param>
    /// <returns>False, with nothing saved, when the unit is gone or was seeded with another stamp after it was picked</returns>
    Task<bool> SaveTokenizedUnitAsync(long unitId, string sourceStamp, string tokenizerVersion,
        IReadOnlyList<StoredBlock> blocks, IReadOnlyList<AnalysisSpan> ambiguous, CancellationToken ct = default);
    /// <summary>
    /// Counts a failed attempt on a pending unit. When the count reaches <paramref name="maxAttempts"/> the unit becomes
    /// failed. Below that it stays pending, and its queue time moves to now so other units go first
    /// </summary>
    /// <returns>The unit's state afterwards</returns>
    Task<ReadingUnitState> RecordFailedAttemptAsync(long unitId, int maxAttempts, CancellationToken ct = default);
    /// <summary>
    /// Moves a pending unit's queue time to now, so other units at its priority go first. Its attempts and priority
    /// stay as they are, and a unit that isn't pending is left alone
    /// </summary>
    Task DeferUnitAsync(long unitId, CancellationToken ct = default);
    /// <returns>The unit with its blocks, in any state, or null when the page has no unit</returns>
    Task<ReadingUnit?> GetUnitAsync(int mangaFileId, int page, CancellationToken ct = default);
    /// <summary>
    /// Raises the priority of the file's pending units to at least <paramref name="priority"/>. Units already at or
    /// above it, and units that aren't pending, are left alone
    /// </summary>
    /// <param name="page">One page, or null for every page of the file</param>
    Task RaiseUnitPriorityAsync(int mangaFileId, int? page, int priority, CancellationToken ct = default);

    /// <returns>Every analysis row of the unit in any state, ordered by block then start offset</returns>
    Task<IReadOnlyList<StoredAnalysis>> GetUnitAnalysisAsync(long unitId, CancellationToken ct = default);
    /// <summary>
    /// Asks for the files' analysis. It records a request per file at the higher of its old priority and
    /// <paramref name="priority"/>, turns the files' idle rows pending at <paramref name="priority"/>, and raises their
    /// pending rows to at least <paramref name="priority"/>. The request outlives re-tokenizing, so rows added later
    /// start pending too
    /// </summary>
    Task RequestAnalysisAsync(IReadOnlyCollection<int> mangaFileIds, int priority, CancellationToken ct = default);
    /// <summary>
    /// Raises pending analysis rows to at least <paramref name="priority"/>. Idle rows stay idle, so this never queues
    /// a book nobody asked to analyze
    /// </summary>
    /// <param name="page">One page, or null for every page of the file</param>
    /// <param name="block">One block of <paramref name="page"/>, or null for the whole page. Ignored when
    /// <paramref name="page"/> is null</param>
    Task RaiseAnalysisPriorityAsync(int mangaFileId, int? page, int? block, int priority, CancellationToken ct = default);
    /// <summary>
    /// Pending analysis rows in queue order, for the runner's passages
    /// </summary>
    /// <param name="limit">The most rows to return</param>
    /// <param name="minPriority">Rows below this priority are left out</param>
    Task<IReadOnlyList<PendingAnalysisRow>> GetPendingAnalysisAsync(int limit, int minPriority, CancellationToken ct = default);
    /// <summary>
    /// Saves answers to pending rows and marks them done, in one transaction. Each answer is checked on its own. One
    /// that names a row that isn't pending, a sense key the word doesn't have, or a reading the word doesn't have is
    /// rejected and the rest are saved. A null sense key or reading is always allowed
    /// </summary>
    /// <returns>How many answers were stored, why each of the others was rejected, and the pages the stored answers
    /// belong to</returns>
    Task<AnalysisStoreOutcome> StoreAnalysisAsync(string generator, string promptVersion,
        IReadOnlyList<AnalysisAnswer> answers, CancellationToken ct = default);
    /// <summary>
    /// Drops the files' analysis requests and sends every one of their rows back to idle with its answer cleared
    /// </summary>
    Task ClearAnalysisAsync(IReadOnlyCollection<int> mangaFileIds, CancellationToken ct = default);

    /// <param name="mangaFileIds">The files to count, or null for the whole store</param>
    Task<ReadingStoreCounts> GetCountsAsync(IReadOnlyCollection<int>? mangaFileIds, CancellationToken ct = default);
}

/// <summary>
/// The queue priorities contract B sets, for units and analysis rows alike
/// </summary>
public static class ReadingPriority
{
    /// <summary>
    /// A paragraph someone tapped a word in
    /// </summary>
    public const int Tapped = 30;
    public const int OpenChapter = 20;
    public const int NextChapter = 10;
    /// <summary>
    /// A whole-book request, and every unit the scanner seeds
    /// </summary>
    public const int WholeBook = 0;
}
