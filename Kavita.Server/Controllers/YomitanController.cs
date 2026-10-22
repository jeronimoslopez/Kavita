using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Kavita.API.Services;
using Kavita.Common;
using Kavita.Models.Constants;
using Kavita.Models.DTOs.Yomitan;
using Kavita.Server.Attributes;
using Microsoft.AspNetCore.Mvc;

namespace Kavita.Server.Controllers;

/// <summary>
/// Backs the Yomitan popup in the book reader. The db/* endpoints mirror Yomitan's DictionaryDatabase methods.
/// </summary>
public class YomitanController(IYomitanDictionaryService dictionaryService, IYomitanOptionsService optionsService,
    IYomitanFetchService fetchService) : BaseApiController
{
    /// <summary>
    /// Mirrors findTermsBulk. matchType is exact, prefix or suffix.
    /// </summary>
    [HttpPost("db/terms-bulk")]
    public async Task<ActionResult<IList<YomitanTermEntryDto>>> FindTermsBulk(YomitanTermsBulkRequestDto dto)
    {
        if (dto.MatchType is not ("exact" or "prefix" or "suffix")) return BadRequest("matchType must be exact, prefix or suffix");
        return Ok(await dictionaryService.FindTermsBulkAsync(dto.TermList, dto.Dictionaries, dto.MatchType, HttpContext.RequestAborted));
    }

    /// <summary>
    /// Mirrors findTermsExactBulk
    /// </summary>
    [HttpPost("db/terms-exact-bulk")]
    public async Task<ActionResult<IList<YomitanTermEntryDto>>> FindTermsExactBulk(YomitanTermsExactBulkRequestDto dto)
    {
        return Ok(await dictionaryService.FindTermsExactBulkAsync(dto.TermList, dto.Dictionaries, HttpContext.RequestAborted));
    }

    /// <summary>
    /// Mirrors findTermsBySequenceBulk
    /// </summary>
    [HttpPost("db/terms-by-sequence-bulk")]
    public async Task<ActionResult<IList<YomitanTermEntryDto>>> FindTermsBySequenceBulk(YomitanTermsBySequenceBulkRequestDto dto)
    {
        return Ok(await dictionaryService.FindTermsBySequenceBulkAsync(dto.Items, HttpContext.RequestAborted));
    }

    /// <summary>
    /// Mirrors findTermMetaBulk
    /// </summary>
    [HttpPost("db/term-meta-bulk")]
    public async Task<ActionResult<IList<YomitanTermMetaDto>>> FindTermMetaBulk(YomitanTermMetaBulkRequestDto dto)
    {
        return Ok(await dictionaryService.FindTermMetaBulkAsync(dto.TermList, dto.Dictionaries, HttpContext.RequestAborted));
    }

    /// <summary>
    /// Mirrors findKanjiBulk
    /// </summary>
    [HttpPost("db/kanji-bulk")]
    public async Task<ActionResult<IList<YomitanKanjiEntryDto>>> FindKanjiBulk(YomitanKanjiBulkRequestDto dto)
    {
        return Ok(await dictionaryService.FindKanjiBulkAsync(dto.KanjiList, dto.Dictionaries, HttpContext.RequestAborted));
    }

    /// <summary>
    /// Mirrors findKanjiMetaBulk
    /// </summary>
    [HttpPost("db/kanji-meta-bulk")]
    public async Task<ActionResult<IList<YomitanKanjiMetaDto>>> FindKanjiMetaBulk(YomitanKanjiBulkRequestDto dto)
    {
        return Ok(await dictionaryService.FindKanjiMetaBulkAsync(dto.KanjiList, dto.Dictionaries, HttpContext.RequestAborted));
    }

    /// <summary>
    /// Mirrors findTagMetaBulk. The result lines up with the request items, null where nothing matched.
    /// </summary>
    [HttpPost("db/tag-meta-bulk")]
    public async Task<ActionResult<IList<YomitanTagDto?>>> FindTagMetaBulk(YomitanTagMetaBulkRequestDto dto)
    {
        return Ok(await dictionaryService.FindTagMetaBulkAsync(dto.Items, HttpContext.RequestAborted));
    }

    /// <summary>
    /// Mirrors getMedia, content is base64
    /// </summary>
    [HttpPost("db/media")]
    public async Task<ActionResult<IList<YomitanMediaDto>>> GetMedia(YomitanMediaBulkRequestDto dto)
    {
        return Ok(await dictionaryService.GetMediaAsync(dto.Items, HttpContext.RequestAborted));
    }

    /// <summary>
    /// Mirrors getDictionaryInfo, the stored Yomitan summary objects
    /// </summary>
    [HttpGet("dictionaries")]
    public async Task<ActionResult<IList<JsonElement>>> GetDictionaries()
    {
        return Ok(await dictionaryService.GetDictionaryInfoAsync(HttpContext.RequestAborted));
    }

    /// <summary>
    /// Progress of the bundled dictionary import
    /// </summary>
    [HttpGet("dictionaries/import-status")]
    public ActionResult<YomitanImportStatusDto> GetImportStatus()
    {
        return Ok(dictionaryService.GetImportStatus());
    }

    /// <summary>
    /// The current user's Yomitan options document
    /// </summary>
    [HttpGet("options")]
    public async Task<ActionResult<YomitanOptionsDto>> GetOptions()
    {
        return Ok(new YomitanOptionsDto
        {
            OptionsJson = await optionsService.GetOptionsJsonAsync(UserId, HttpContext.RequestAborted),
        });
    }

    /// <summary>
    /// Saves the current user's Yomitan options document. Must be a JSON object of at most 2 MB.
    /// </summary>
    [HttpPost("options")]
    [DisallowRole(PolicyConstants.ReadOnlyRole)]
    [RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<ActionResult> SaveOptions(YomitanOptionsDto dto)
    {
        if (string.IsNullOrEmpty(dto.OptionsJson)) return BadRequest("optionsJson is required");

        try
        {
            await optionsService.SaveOptionsJsonAsync(UserId, dto.OptionsJson, HttpContext.RequestAborted);
        }
        catch (KavitaException ex)
        {
            return BadRequest(ex.Message);
        }
        return Ok();
    }

    /// <summary>
    /// Fetch proxy for Yomitan audio sources that block cross-origin requests. Public https hosts only.
    /// </summary>
    [HttpPost("fetch")]
    public async Task<ActionResult<YomitanFetchResponseDto>> Fetch(YomitanFetchRequestDto dto)
    {
        try
        {
            return Ok(await fetchService.FetchAsync(dto, HttpContext.RequestAborted));
        }
        catch (KavitaException ex)
        {
            return BadRequest(ex.Message);
        }
    }
}
