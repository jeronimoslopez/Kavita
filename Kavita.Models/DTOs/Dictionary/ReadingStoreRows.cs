using System.Collections.Generic;

namespace Kavita.Models.DTOs.Dictionary;
#nullable enable

/// <summary>
/// What a unit of text was cut from. Stored as the lowercase name
/// </summary>
public enum ReadingUnitKind
{
    /// <summary>
    /// One EPUB spine item, which is one page of the book reader
    /// </summary>
    Epub = 0,
}

/// <summary>
/// Where a unit is in the tokenization queue. Stored as the lowercase name
/// </summary>
public enum ReadingUnitState
{
    /// <summary>
    /// Waiting to be tokenized. A unit whose file changed goes back to this state and keeps its old blocks until then
    /// </summary>
    Pending = 0,
    Done = 1,
    /// <summary>
    /// Tokenizing failed as many times as the caller allows. It stays out of the queue until its file changes
    /// </summary>
    Failed = 2,
    /// <summary>
    /// Tokenized from the file as it is now by an older sidecar version, and queued to be tokenized again. Its blocks
    /// are still served until the new ones are saved. If tokenizing fails as many times as the caller allows, it goes
    /// back to done with its old blocks. Builds from before this state can't parse it, so rolling back needs every
    /// refresh unit set to done first
    /// </summary>
    Refresh = 3,
}

/// <summary>
/// Where an occurrence is in the AI analysis queue. Stored as the lowercase name. Only files with an analysis request
/// have rows, so there's no state for a book nobody asked for
/// </summary>
public enum AnalysisState
{
    Pending = 1,
    Done = 2,
}

public enum AnalysisRejectReason
{
    /// <summary>
    /// The id doesn't exist, or the row isn't pending
    /// </summary>
    NotPending = 0,
    /// <summary>
    /// The sense key isn't one of the word's senses
    /// </summary>
    SenseNotOffered = 1,
    /// <summary>
    /// The reading isn't one of the word's readings
    /// </summary>
    ReadingNotOffered = 2,
    /// <summary>
    /// The same id came earlier in the same batch, and the earlier answer is the one kept
    /// </summary>
    Duplicate = 3,
}

/// <summary>
/// A unit picked from the tokenization queue
/// </summary>
/// <param name="Page">0-based page, as the book reader counts them</param>
/// <param name="SourceStamp">The stamp the unit was seeded with, which <c>SaveTokenizedUnitAsync</c> expects back</param>
/// <param name="Attempts">Failed attempts so far</param>
public sealed record ReadingUnitRef(long Id, ReadingUnitKind Kind, int MangaFileId, int Page, string SourceStamp, int Attempts);

/// <summary>
/// A unit with its stored blocks
/// </summary>
/// <param name="TokenizerVersion">The sidecar version the blocks came from, or null when the unit was never tokenized</param>
/// <param name="Blocks">Empty when the unit was never tokenized. A pending unit whose file changed still has its old blocks</param>
public sealed record ReadingUnit(long Id, ReadingUnitKind Kind, int MangaFileId, int Page, ReadingUnitState State,
    string SourceStamp, string? TokenizerVersion, IReadOnlyList<StoredBlock> Blocks);

/// <summary>
/// One block as reading.db stores it
/// </summary>
/// <param name="Index">The block's <c>data-kb</c> value</param>
/// <param name="Tokens">Word ids in <see cref="TokenDto.W"/> are reading.db word ids</param>
public sealed record StoredBlock(int Index, string Text, IReadOnlyList<TokenDto> Tokens)
{
    /// <summary>
    /// Word ids in <see cref="PhraseDto.W"/> are reading.db word ids. Empty for a block tokenized by a sidecar that
    /// sends no phrases. Phrases never get analysis rows
    /// </summary>
    public IReadOnlyList<PhraseDto> Phrases { get; init; } = [];
    /// <summary>
    /// The block's furigana in document order, with offsets into <see cref="Text"/>. Empty for a block stored before
    /// furigana was kept
    /// </summary>
    public IReadOnlyList<RubyDto> Ruby { get; init; } = [];
}

/// <summary>
/// One way a book's furigana reads a word in dictionary form, summed over the pages asked about
/// </summary>
/// <param name="Reading">The reading in hiragana</param>
/// <param name="Printed">The reading as the book prints it, the most frequent printed form when the pages print it
/// more than one way</param>
/// <param name="Count">How many top-level tokens of the word the furigana reads this way</param>
public sealed record ReadingMarkTotal(string Reading, string Printed, int Count);

/// <summary>
/// How a word is used and marked over the pages asked about
/// </summary>
/// <param name="Uses">How many top-level tokens point at the word, marked or not</param>
/// <param name="Readings">The marked readings, most counted first and then by reading. Empty when no token of the
/// word is marked</param>
public sealed record WordMarkTotals(long WordId, int Uses, IReadOnlyList<ReadingMarkTotal> Readings);

/// <summary>
/// A row of reading.db's word table
/// </summary>
/// <param name="Jlpt">1 (N1) to 5 (N5), or null when the word isn't listed</param>
public sealed record StoredWord(long Id, string Lemma, string Pos, bool IsProper, string Reading, int? Jlpt,
    IReadOnlyList<string> Readings, IReadOnlyList<SenseDto> Senses, string LexiconVersion);

/// <summary>
/// A row of reading.db's analysis table
/// </summary>
/// <param name="SenseKey">Null until answered, and null in an answer that found no sense fits</param>
/// <param name="Generator">Null until answered</param>
public sealed record StoredAnalysis(long Id, long UnitId, int Block, int S, int E, long WordId, AnalysisState State,
    int Priority, string? SenseKey, string? Reading, string? Generator, string? PromptVersion);

/// <summary>
/// A pending analysis row with what a passage needs around it
/// </summary>
/// <param name="BlockText">The whole text of the row's block</param>
public sealed record PendingAnalysisRow(long Id, long UnitId, int MangaFileId, int Page, int Block, int S, int E,
    long WordId, int Priority, string BlockText);

/// <param name="Reading">Null to store no reading</param>
/// <param name="SenseKey">Null when none of the offered senses fit</param>
public sealed record AnalysisAnswer(long Id, string? SenseKey, string? Reading);

public sealed record AnalysisRejection(long Id, AnalysisRejectReason Reason);

public sealed record AnalysisStoreOutcome(int Stored, IReadOnlyList<AnalysisRejection> Rejected)
{
    /// <summary>
    /// The pages that gained at least one answer in this call, each once, ordered by file then page
    /// </summary>
    public IReadOnlyList<AnalysisStoredPage> StoredPages { get; init; } = [];
}

/// <summary>
/// A page that gained answers, so the API can tell readers of that page to fetch it again
/// </summary>
/// <param name="Page">0-based page, as the book reader counts them</param>
public sealed record AnalysisStoredPage(int MangaFileId, int Page);

/// <summary>
/// Row counts by state, for status displays. <see cref="UnitsPending"/> includes units waiting for a refresh, since
/// they're in the queue too
/// </summary>
public sealed record ReadingStoreCounts(int UnitsPending, int UnitsDone, int UnitsFailed, int AnalysisPending,
    int AnalysisDone);
