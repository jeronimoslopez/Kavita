using System.Collections.Generic;
using System.Text.Json;

namespace Kavita.Models.DTOs.Yomitan;

/// <summary>
/// Mirrors Yomitan's <c>TermEntry</c> (the result of <c>DictionaryDatabase._createTerm</c>)
/// </summary>
public sealed record YomitanTermEntryDto
{
    /// <summary>
    /// Position of the matching item in the request list
    /// </summary>
    public int Index { get; init; }
    /// <summary>
    /// exact, prefix or suffix
    /// </summary>
    public required string MatchType { get; init; }
    /// <summary>
    /// term, reading or sequence
    /// </summary>
    public required string MatchSource { get; init; }
    public required string Term { get; init; }
    public required string Reading { get; init; }
    public required IList<string> DefinitionTags { get; init; }
    public required IList<string> TermTags { get; init; }
    public required IList<string> Rules { get; init; }
    /// <summary>
    /// The glossary list as stored by the importer (strings, deinflection tuples, image and structured-content objects)
    /// </summary>
    public JsonElement Definitions { get; init; }
    public double Score { get; init; }
    public required string Dictionary { get; init; }
    public long Id { get; init; }
    /// <summary>
    /// -1 when the dictionary entry has no sequence
    /// </summary>
    public long Sequence { get; init; }
}

/// <summary>
/// Mirrors Yomitan's <c>TermMeta</c> (freq, pitch or ipa)
/// </summary>
public sealed record YomitanTermMetaDto
{
    public int Index { get; init; }
    public required string Term { get; init; }
    public required string Mode { get; init; }
    public JsonElement Data { get; init; }
    public required string Dictionary { get; init; }
}

/// <summary>
/// Mirrors Yomitan's <c>KanjiEntry</c>
/// </summary>
public sealed record YomitanKanjiEntryDto
{
    public int Index { get; init; }
    public required string Character { get; init; }
    public required IList<string> Onyomi { get; init; }
    public required IList<string> Kunyomi { get; init; }
    public required IList<string> Tags { get; init; }
    public JsonElement Definitions { get; init; }
    /// <summary>
    /// Object of stat name to value, an empty object when the dictionary has none
    /// </summary>
    public JsonElement Stats { get; init; }
    public required string Dictionary { get; init; }
}

/// <summary>
/// Mirrors Yomitan's <c>KanjiMeta</c>
/// </summary>
public sealed record YomitanKanjiMetaDto
{
    public int Index { get; init; }
    public required string Character { get; init; }
    public required string Mode { get; init; }
    public JsonElement Data { get; init; }
    public required string Dictionary { get; init; }
}

/// <summary>
/// Mirrors Yomitan's <c>Tag</c>
/// </summary>
public sealed record YomitanTagDto
{
    public required string Name { get; init; }
    public required string Category { get; init; }
    public double Order { get; init; }
    public required string Notes { get; init; }
    public double Score { get; init; }
    public required string Dictionary { get; init; }
}

/// <summary>
/// Mirrors Yomitan's <c>Media</c> with the content encoded as base64, which is what Yomitan's backend hands the display
/// </summary>
public sealed record YomitanMediaDto
{
    public int Index { get; init; }
    public required string Dictionary { get; init; }
    public required string Path { get; init; }
    public required string MediaType { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public required string Content { get; init; }
}
