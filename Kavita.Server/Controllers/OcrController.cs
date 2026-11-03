using System.Threading.Tasks;
using Kavita.API.Services;
using Kavita.Models.Constants;
using Kavita.Models.DTOs.Ocr;
using Kavita.Server.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Kavita.Server.Controllers;

/// <summary>
/// OCR text for scanned PDF pages, and the admin controls that queue or remove it
/// </summary>
public class OcrController(IOcrService ocrService) : BaseApiController
{
    /// <summary>
    /// Where the chapter's PDF stands in OCR, and which pages have text
    /// </summary>
    /// <returns>No content when the chapter has no PDF</returns>
    [ChapterAccess]
    [HttpGet("chapter")]
    public async Task<ActionResult<OcrChapterStatusDto>> GetChapterStatus(int chapterId)
    {
        var status = await ocrService.GetChapterStatusAsync(chapterId, HttpContext.RequestAborted);
        return status == null ? NoContent() : Ok(status);
    }

    /// <summary>
    /// The OCR lines for one page
    /// </summary>
    /// <param name="chapterId"></param>
    /// <param name="pageNum">1-based, matching pdf.js</param>
    /// <returns>No content when the page has no OCR text (yet)</returns>
    [ChapterAccess]
    [HttpGet("page")]
    public async Task<ActionResult<OcrPageDto>> GetPage(int chapterId, int pageNum)
    {
        var page = await ocrService.GetPageAsync(chapterId, pageNum, HttpContext.RequestAborted);
        return page == null ? NoContent() : Ok(page);
    }

    /// <summary>
    /// Moves the chapter ahead in the OCR queue. The reader calls this when the chapter is opened
    /// </summary>
    [ChapterAccess]
    [HttpPost("chapter/prioritize")]
    public async Task<ActionResult> PrioritizeChapter(int chapterId)
    {
        await ocrService.PrioritizeChapterAsync(chapterId, HttpContext.RequestAborted);
        return Ok();
    }

    /// <summary>
    /// Queues the series' PDFs for OCR ahead of everything else
    /// </summary>
    /// <returns>How many files were queued</returns>
    [Authorize(Policy = PolicyGroups.AdminPolicy)]
    [HttpPost("series/run")]
    public async Task<ActionResult<int>> RunSeries(OcrSeriesRequestDto dto)
    {
        return Ok(await ocrService.RunSeriesAsync(dto.SeriesId, dto.ReplaceExistingText, HttpContext.RequestAborted));
    }

    /// <summary>
    /// Deletes the series' OCR text. Rescans won't queue it again until it's run by hand
    /// </summary>
    [Authorize(Policy = PolicyGroups.AdminPolicy)]
    [HttpPost("series/remove")]
    public async Task<ActionResult> RemoveSeries(OcrSeriesRequestDto dto)
    {
        await ocrService.RemoveSeriesAsync(dto.SeriesId, HttpContext.RequestAborted);
        return Ok();
    }
}
