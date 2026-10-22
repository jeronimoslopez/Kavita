using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kavita.Models.DTOs.Yomitan;

namespace Kavita.API.Services;

/// <summary>
/// Read side of the bundled Yomitan dictionaries. Each method mirrors the <c>DictionaryDatabase</c> method of the same
/// name in Yomitan and returns the already-shaped objects the translator expects.
/// </summary>
public interface IYomitanDictionaryService
{
    /// <summary>
    /// Mirrors <c>findTermsBulk</c>. Matches on expression and reading, deduplicated by row id.
    /// </summary>
    /// <param name="termList"></param>
    /// <param name="dictionaries">Dictionary titles to include</param>
    /// <param name="matchType">exact, prefix or suffix</param>
    /// <param name="ct"></param>
    Task<IList<YomitanTermEntryDto>> FindTermsBulkAsync(IList<string> termList, IList<string> dictionaries, string matchType, CancellationToken ct = default);
    /// <summary>
    /// Mirrors <c>findTermsExactBulk</c>
    /// </summary>
    Task<IList<YomitanTermEntryDto>> FindTermsExactBulkAsync(IList<YomitanTermExactRequestDto> termList, IList<string> dictionaries, CancellationToken ct = default);
    /// <summary>
    /// Mirrors <c>findTermsBySequenceBulk</c>
    /// </summary>
    Task<IList<YomitanTermEntryDto>> FindTermsBySequenceBulkAsync(IList<YomitanSequenceQueryDto> items, CancellationToken ct = default);
    /// <summary>
    /// Mirrors <c>findTermMetaBulk</c>
    /// </summary>
    Task<IList<YomitanTermMetaDto>> FindTermMetaBulkAsync(IList<string> termList, IList<string> dictionaries, CancellationToken ct = default);
    /// <summary>
    /// Mirrors <c>findKanjiBulk</c>
    /// </summary>
    Task<IList<YomitanKanjiEntryDto>> FindKanjiBulkAsync(IList<string> kanjiList, IList<string> dictionaries, CancellationToken ct = default);
    /// <summary>
    /// Mirrors <c>findKanjiMetaBulk</c>
    /// </summary>
    Task<IList<YomitanKanjiMetaDto>> FindKanjiMetaBulkAsync(IList<string> kanjiList, IList<string> dictionaries, CancellationToken ct = default);
    /// <summary>
    /// Mirrors <c>findTagMetaBulk</c>. The result is aligned with <paramref name="items"/>, null where nothing matched.
    /// </summary>
    Task<IList<YomitanTagDto?>> FindTagMetaBulkAsync(IList<YomitanTagQueryDto> items, CancellationToken ct = default);
    /// <summary>
    /// Mirrors <c>getMedia</c>, content is base64
    /// </summary>
    Task<IList<YomitanMediaDto>> GetMediaAsync(IList<YomitanMediaRequestDto> items, CancellationToken ct = default);
    /// <summary>
    /// Mirrors <c>getDictionaryInfo</c>. Returns the stored summary objects untouched.
    /// </summary>
    Task<IList<JsonElement>> GetDictionaryInfoAsync(CancellationToken ct = default);
    /// <summary>
    /// Progress of the startup import of bundled dictionaries
    /// </summary>
    YomitanImportStatusDto GetImportStatus();
}
