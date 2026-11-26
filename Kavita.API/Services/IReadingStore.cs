using System;
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
/// first, then by the time the row was queued or last raised, oldest first, then by id. While an upgrade rewrites
/// reading.db's rows, <see cref="GetUnitAsync"/> returns null and every other method throws
/// <see cref="ReadingStoreNotReadyException"/>.
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
    /// get a pending one, and units past the end are deleted with their analysis, reading marks and word uses. A unit
    /// with a different stamp goes back to pending with the new stamp and keeps its blocks, analysis, reading marks and
    /// word uses until it's tokenized again. A failed unit
    /// with the same stamp stays failed
    /// </summary>
    /// <param name="sourceStamp">Changes whenever the file's content does, such as its last write time in UTC ticks</param>
    /// <param name="priority">Priority of the units this call creates or sends back to pending</param>
    Task SeedFileAsync(int mangaFileId, int pageCount, string sourceStamp, int priority, CancellationToken ct = default);
    /// <summary>
    /// Deletes the files' units, analysis rows, reading marks, word uses and analysis requests. Words stay, because
    /// other files share them
    /// </summary>
    Task DeleteFilesAsync(IReadOnlyCollection<int> mangaFileIds, CancellationToken ct = default);

    /// <returns>The pending or refresh unit to tokenize next in queue order, or null when there's none</returns>
    Task<ReadingUnitRef?> GetNextPendingUnitAsync(CancellationToken ct = default);
    /// <summary>
    /// Sends the file's done units whose blocks came from a sidecar version other than
    /// <paramref name="tokenizerVersion"/> to <see cref="ReadingUnitState.Refresh"/>, at least at
    /// <paramref name="priority"/>. They keep serving their blocks and analysis until the new blocks are saved. Units
    /// saved with no blocks and an empty version are left alone, and so are pending and failed units
    /// </summary>
    /// <returns>How many units were queued</returns>
    Task<int> RefreshUnitsAsync(int mangaFileId, string tokenizerVersion, int priority, CancellationToken ct = default);
    /// <summary>
    /// Stores a tokenized unit and marks it done, whether it was pending or waiting for a refresh, and replaces its analysis rows, in one transaction. When the file
    /// has no analysis request the unit is left with no rows. When it has one, the unit's spans are its top-level
    /// tokens whose word is ambiguous, which is the rule <see cref="SidecarWordDto.IsAmbiguous"/> applies to the
    /// stored word. A row whose (block, s, e, word id) is still a span keeps its state, priority and answer, other
    /// rows are deleted, and a span with no row gets one, pending at the request's priority. Parts of compounds never
    /// get a row. The same transaction replaces the unit's reading marks and word uses with those its top-level
    /// tokens give: a use per token with a word, and a mark per token the block's furigana reads in dictionary form
    /// </summary>
    /// <param name="sourceStamp">The stamp from the <see cref="ReadingUnitRef"/> that was tokenized</param>
    /// <param name="blocks">Every block of the page in index order, with reading.db word ids in its tokens</param>
    /// <returns>False, with nothing saved, when the unit is gone or was seeded with another stamp after it was picked</returns>
    Task<bool> SaveTokenizedUnitAsync(long unitId, string sourceStamp, string tokenizerVersion,
        IReadOnlyList<StoredBlock> blocks, CancellationToken ct = default);
    /// <summary>
    /// Counts a failed attempt on a pending or refresh unit. When the count reaches <paramref name="maxAttempts"/> a
    /// pending unit becomes failed, and a refresh unit goes back to done with its old blocks and its attempts at 0. Below
    /// that it keeps its state, and its queue time moves to now so other units go first
    /// </summary>
    /// <returns>The unit's state afterwards</returns>
    Task<ReadingUnitState> RecordFailedAttemptAsync(long unitId, int maxAttempts, CancellationToken ct = default);
    /// <summary>
    /// Moves a pending or refresh unit's queue time to now, so other units at its priority go first. Its attempts and
    /// priority stay as they are, and a unit in another state is left alone
    /// </summary>
    Task DeferUnitAsync(long unitId, CancellationToken ct = default);
    /// <returns>The unit with its blocks, in any state, or null when the page has no unit or reading.db is being
    /// upgraded</returns>
    Task<ReadingUnit?> GetUnitAsync(int mangaFileId, int page, CancellationToken ct = default);
    /// <summary>
    /// Raises the priority of the file's pending and refresh units to at least <paramref name="priority"/>. Units
    /// already at or above it, and units in other states, are left alone
    /// </summary>
    /// <param name="page">One page, or null for every page of the file</param>
    Task RaiseUnitPriorityAsync(int mangaFileId, int? page, int priority, CancellationToken ct = default);

    /// <returns>Every analysis row of the unit in any state, ordered by block then start offset. Empty when the file
    /// has no analysis request</returns>
    Task<IReadOnlyList<StoredAnalysis>> GetUnitAnalysisAsync(long unitId, CancellationToken ct = default);
    /// <summary>
    /// Asks for the files' analysis, one transaction per file. It records the file's request at the higher of its old
    /// priority and <paramref name="priority"/>, and raises the file's pending rows to at least
    /// <paramref name="priority"/>. Every unit that already has blocks gets a pending row per span at the request's
    /// priority, with spans picked as <see cref="SaveTokenizedUnitAsync"/> picks them. Rows that exist, done ones
    /// included, are left as they are, so asking twice changes nothing but priorities. The request outlives
    /// re-tokenizing, so units tokenized later get their rows too
    /// </summary>
    Task RequestAnalysisAsync(IReadOnlyCollection<int> mangaFileIds, int priority, CancellationToken ct = default);
    /// <summary>
    /// Raises pending analysis rows to at least <paramref name="priority"/>. A file without a request has no rows, so
    /// this never queues a book nobody asked to analyze
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
    /// Deletes the files' analysis requests and every one of their analysis rows, answers included. Units and words stay
    /// </summary>
    Task ClearAnalysisAsync(IReadOnlyCollection<int> mangaFileIds, CancellationToken ct = default);

    /// <summary>
    /// How one file's furigana reads the words, summed over its pages
    /// </summary>
    /// <returns>An entry per word of <paramref name="wordIds"/> that a top-level token of the file uses, with the
    /// file's marked readings of it. Pages tokenized before furigana was kept count for neither</returns>
    Task<IReadOnlyDictionary<long, WordMarkTotals>> GetReadingMarksAsync(int mangaFileId,
        IReadOnlyCollection<long> wordIds, CancellationToken ct = default);
    /// <summary>
    /// How the files' furigana reads the words, summed over every page of every file
    /// </summary>
    /// <returns>As the one-file overload returns, summed over the files</returns>
    Task<IReadOnlyDictionary<long, WordMarkTotals>> GetReadingMarksAsync(IReadOnlyCollection<int> mangaFileIds,
        IReadOnlyCollection<long> wordIds, CancellationToken ct = default);

    /// <param name="mangaFileIds">The files to count, or null for the whole store</param>
    Task<ReadingStoreCounts> GetCountsAsync(IReadOnlyCollection<int>? mangaFileIds, CancellationToken ct = default);
}

/// <summary>
/// reading.db is being upgraded and its existing rows rewritten, which can take a minute or more after an update.
/// Try again later
/// </summary>
public class ReadingStoreNotReadyException() : Exception("The dictionary's reading store is being upgraded");

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
