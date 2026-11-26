using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Kavita.API.Database;
using Kavita.API.Services;
using Kavita.Common;
using Kavita.Models.Constants;
using Kavita.Models.DTOs.Dictionary;
using Kavita.Server.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Kavita.Server.Controllers;

/// <summary>
/// Tokens and word lookups for the EPUB reader's dictionary popup, and the queue the AI analysis runner works from
/// </summary>
public class DictionaryController(IDictionaryService dictionaryService, IUnitOfWork unitOfWork) : BaseApiController
{
    public const int DefaultPassageLimit = 4;
    public const int MaxPassageLimit = 20;

    /// <summary>
    /// The page's blocks with their tokens and phrases, the words they point at with their senses, and the stored AI
    /// answers, with each top-level word's hint and the user's saved choice. That's everything <see cref="Lookup"/> reads,
    /// so the reader can answer a tap on this page itself. The saved choices make the answer the user's own, so it's
    /// never cached
    /// </summary>
    /// <param name="page">0-based, as the book reader counts pages</param>
    /// <returns>No content when the page isn't tokenized yet. It moves up the queue, and DictionaryPagesReady follows.
    /// Also no content while reading.db is being upgraded</returns>
    [ChapterAccess]
    [HttpGet("epub-page")]
    public async Task<ActionResult<EpubPageTokensDto>> GetEpubPage(int chapterId, int page)
    {
        var tokens = await dictionaryService.GetEpubPageAsync(UserId, chapterId, page, HttpContext.RequestAborted);
        return tokens == null ? NoContent() : Ok(tokens);
    }

    /// <summary>
    /// The tapped word with its senses, its AI answer and its parts
    /// </summary>
    /// <returns>No content when nothing can be looked up at that offset, which the reader treats as a miss</returns>
    [HttpPost("lookup")]
    public async Task<ActionResult<DictionaryLookupResultDto>> Lookup(DictionaryLookupRequestDto dto)
    {
        var ct = HttpContext.RequestAborted;
        if (!User.IsInRole(PolicyConstants.AdminRole) &&
            !await unitOfWork.UserRepository.HasAccessToChapter(UserId, dto.ChapterId, ct)) return NotFound();

        var result = await dictionaryService.LookupAsync(UserId, dto, ct);
        return result == null ? NoContent() : Ok(result);
    }

    /// <summary>
    /// Saves the reading the user picked for a word in a series, in place of any saved before. Later lookups of the
    /// word anywhere in the series open on it
    /// </summary>
    /// <returns>400 when the reading isn't a reading of one of the word's groups, 404 without access to the series, and
    /// 503 while reading.db is being upgraded</returns>
    [HttpPut("reading-choice")]
    public async Task<ActionResult> SaveReadingChoice(ReadingChoiceDto dto)
    {
        var ct = HttpContext.RequestAborted;
        if (!User.IsInRole(PolicyConstants.AdminRole) &&
            !await unitOfWork.UserRepository.HasAccessToSeries(UserId, dto.SeriesId, ct)) return NotFound();

        try
        {
            await dictionaryService.SaveReadingChoiceAsync(UserId, dto, ct);
        }
        catch (KavitaException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (ReadingStoreNotReadyException ex)
        {
            return NotReady(ex);
        }
        return Ok();
    }

    /// <summary>
    /// Clears the user's saved reading for a word in a series. Clearing one that isn't saved succeeds
    /// </summary>
    /// <returns>503 while reading.db is being upgraded</returns>
    [SeriesAccess]
    [HttpDelete("reading-choice")]
    public async Task<ActionResult> ClearReadingChoice(int seriesId, long wordId)
    {
        try
        {
            await dictionaryService.ClearReadingChoiceAsync(UserId, seriesId, wordId, HttpContext.RequestAborted);
        }
        catch (ReadingStoreNotReadyException ex)
        {
            return NotReady(ex);
        }
        return Ok();
    }

    /// <summary>
    /// Moves the chapter and the one after it ahead in the tokenizing and analysis queues. The reader calls this when
    /// the chapter opens
    /// </summary>
    [ChapterAccess]
    [HttpPost("chapter/prioritize")]
    public async Task<ActionResult> PrioritizeChapter(int chapterId)
    {
        try
        {
            await dictionaryService.PrioritizeChapterAsync(chapterId, HttpContext.RequestAborted);
        }
        catch (ReadingStoreNotReadyException)
        {
            // The worker reaches the chapter on its own once the upgrade is done
        }
        return Ok();
    }

    /// <summary>
    /// Queues AI analysis of every EPUB in the series, behind anything a reader has open
    /// </summary>
    /// <returns>How many files were queued, or 503 while reading.db is being upgraded</returns>
    [Authorize(Policy = PolicyGroups.AdminPolicy)]
    [HttpPost("series/analyze")]
    public async Task<ActionResult<int>> AnalyzeSeries(DictionarySeriesRequestDto dto)
    {
        try
        {
            return Ok(await dictionaryService.AnalyzeSeriesAsync(dto.SeriesId, HttpContext.RequestAborted));
        }
        catch (ReadingStoreNotReadyException ex)
        {
            return NotReady(ex);
        }
    }

    /// <summary>
    /// Clears the series' AI answers and its analysis request. Its tokens stay, so lookups keep working
    /// </summary>
    [Authorize(Policy = PolicyGroups.AdminPolicy)]
    [HttpPost("series/remove")]
    public async Task<ActionResult> RemoveSeries(DictionarySeriesRequestDto dto)
    {
        try
        {
            await dictionaryService.RemoveSeriesAsync(dto.SeriesId, HttpContext.RequestAborted);
        }
        catch (ReadingStoreNotReadyException ex)
        {
            return NotReady(ex);
        }
        return Ok();
    }

    /// <summary>
    /// Passages of book text with the occurrences in them that still need an AI answer, highest priority first
    /// </summary>
    /// <param name="limit">Passages to return, 4 when left out and at most 20</param>
    /// <param name="minPriority">Leaves out rows below this priority</param>
    /// <returns>No content when nothing is waiting or reading.db is being upgraded</returns>
    [Authorize(Policy = PolicyGroups.AdminPolicy)]
    [HttpGet("analysis/pending")]
    public async Task<ActionResult<IList<AnalysisPassageDto>>> GetPendingAnalysis(int? limit, int minPriority = 0)
    {
        var count = Math.Clamp(limit ?? DefaultPassageLimit, 1, MaxPassageLimit);
        try
        {
            var passages = await dictionaryService.GetPendingAnalysisAsync(count, minPriority, HttpContext.RequestAborted);
            return passages.Count == 0 ? NoContent() : Ok(passages);
        }
        catch (ReadingStoreNotReadyException)
        {
            return NoContent();
        }
    }

    /// <summary>
    /// Stores the runner's answers. Each answer is checked on its own, so one bad answer doesn't lose the rest
    /// </summary>
    /// <returns>How many were stored, and why each of the others wasn't. 503 while reading.db is being upgraded, with
    /// nothing stored</returns>
    [Authorize(Policy = PolicyGroups.AdminPolicy)]
    [HttpPost("analysis")]
    public async Task<ActionResult<AnalysisStoreResultDto>> StoreAnalysis(AnalysisStoreRequestDto dto)
    {
        try
        {
            return Ok(await dictionaryService.StoreAnalysisAsync(dto, HttpContext.RequestAborted));
        }
        catch (KavitaException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (ReadingStoreNotReadyException ex)
        {
            return NotReady(ex);
        }
    }

    private ObjectResult NotReady(ReadingStoreNotReadyException ex) =>
        StatusCode(StatusCodes.Status503ServiceUnavailable, ex.Message);
}
