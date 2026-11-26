using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kavita.Models.DTOs.Dictionary;

namespace Kavita.API.Services;

/// <summary>
/// Contract C, behind <c>api/dictionary</c>. One method per endpoint. The controller checks access before it calls
/// any of them, so they don't check it again
/// </summary>
public interface IDictionaryService
{
    /// <summary>
    /// <c>GET epub-page</c>. The page's blocks, tokens, word briefs and stored analysis, with each top-level word's
    /// hint and the user's saved choice. When the page isn't tokenized yet, its unit goes up to
    /// <see cref="ReadingPriority.OpenChapter"/> and the worker is woken
    /// </summary>
    /// <param name="userId">The requesting user, whose saved choices the answer carries</param>
    /// <param name="page">0-based, as the book reader counts pages</param>
    /// <returns>Null when the page isn't tokenized yet, which the controller answers with 204. A page waiting to be
    /// tokenized again by a newer sidecar returns its current tokens</returns>
    Task<EpubPageTokensDto?> GetEpubPageAsync(int userId, int chapterId, int page, CancellationToken ct = default);
    /// <summary>
    /// <c>POST lookup</c>. The tapped word, its senses, its stored analysis, its parts and the phrases it's part of, or
    /// with <see cref="DictionaryLookupRequestDto.Phrase"/> set, the phrase's word and senses. Raises the pending
    /// analysis of the tapped block to <see cref="ReadingPriority.Tapped"/>. The answer's readings come from the
    /// resolver, with the user's saved choice for the word in the chapter's series
    /// </summary>
    /// <returns>Null when the page isn't tokenized, or no top-level token starts at the offset, or the sub index is out of
    /// range, or the token has no word. For a phrase, null when the index is out of range or the offset falls outside
    /// the phrase</returns>
    Task<DictionaryLookupResultDto?> LookupAsync(int userId, DictionaryLookupRequestDto request, CancellationToken ct = default);
    /// <summary>
    /// <c>PUT reading-choice</c>. Saves the reading the user picked for the word in the series, in place of any saved
    /// before. Throws a KavitaException, which the controller answers with 400, when the word doesn't exist or the
    /// reading isn't a reading of one of its groups
    /// </summary>
    Task SaveReadingChoiceAsync(int userId, ReadingChoiceDto choice, CancellationToken ct = default);
    /// <summary>
    /// <c>DELETE reading-choice</c>. Clears the user's saved reading for the word in the series
    /// </summary>
    Task ClearReadingChoiceAsync(int userId, int seriesId, long wordId, CancellationToken ct = default);
    /// <summary>
    /// <c>POST chapter/prioritize</c>, called when a chapter opens. Queues tokenization for it and the next chapter. It
    /// raises their pending analysis to the same priorities, and when <c>DictionaryAnalysisOnOpen</c> is on it also
    /// requests their analysis
    /// </summary>
    Task PrioritizeChapterAsync(int chapterId, CancellationToken ct = default);
    /// <summary>
    /// <c>POST series/analyze</c>. Requests analysis of every EPUB in the series at <see cref="ReadingPriority.WholeBook"/>
    /// </summary>
    /// <returns>How many files were requested</returns>
    Task<int> AnalyzeSeriesAsync(int seriesId, CancellationToken ct = default);
    /// <summary>
    /// <c>POST series/remove</c>. Clears the series' analysis and its request. Tokens stay, so lookups keep working
    /// </summary>
    Task RemoveSeriesAsync(int seriesId, CancellationToken ct = default);
    /// <summary>
    /// <c>GET analysis/pending</c>. Builds passages of about 2,000 characters from the pending rows in queue order. A
    /// passage holds whole blocks of one page, and each item's offsets are offsets into the passage's text
    /// </summary>
    /// <param name="limit">The most passages to return</param>
    /// <param name="minPriority">Rows below this priority are left out</param>
    Task<IList<AnalysisPassageDto>> GetPendingAnalysisAsync(int limit, int minPriority, CancellationToken ct = default);
    /// <summary>
    /// <c>POST analysis</c>. Stores the runner's answers with the rule shimen's <c>annotations.store</c> applies. It rejects
    /// an id that isn't pending, a sense key that wasn't offered and a reading that wasn't offered, and stores the rest.
    /// Sends <c>DictionaryAnalysisReady</c> for the pages that got answers
    /// </summary>
    Task<AnalysisStoreResultDto> StoreAnalysisAsync(AnalysisStoreRequestDto request, CancellationToken ct = default);
}
