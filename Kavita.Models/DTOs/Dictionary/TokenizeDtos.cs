using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Kavita.Models.DTOs.Dictionary;
#nullable enable

/// <summary>
/// One block of text sent to the text sidecar's <c>POST /tokenize</c>. The contract is
/// <c>text-sidecar-contract/tokenize-request.schema.json</c>
/// </summary>
/// <param name="Id">Echoed back on the block's tokens. Kavita sends the block's index as a string</param>
public sealed record TokenizeBlockDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string Text);

public sealed record TokenizeRequestDto
{
    [JsonPropertyName("blocks")]
    public IList<TokenizeBlockDto> Blocks { get; init; } = [];
}

/// <summary>
/// The text sidecar's answer to <c>POST /tokenize</c>. The contract is <c>text-sidecar-contract/tokenize-response.schema.json</c>
/// </summary>
public sealed record TokenizeResponseDto
{
    /// <summary>
    /// Names the tokenizer, dictionary, word rules and lexicon that made this answer, such as
    /// <c>sudachipy=..;sudachidict_full=..;modes=C/A;words=6;lexicon=..;sidecar=1</c>
    /// </summary>
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;
    /// <summary>
    /// Every word the tokens point at. A token's <see cref="TokenDto.W"/> is an index into this list
    /// </summary>
    [JsonPropertyName("words")]
    public IList<SidecarWordDto> Words { get; init; } = [];
    /// <summary>
    /// One entry per block sent, in the same order
    /// </summary>
    [JsonPropertyName("blocks")]
    public IList<TokenizedBlockDto> Blocks { get; init; } = [];
}

public sealed record TokenizedBlockDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;
    [JsonPropertyName("tokens")]
    public IList<TokenDto> Tokens { get; init; } = [];
}

/// <summary>
/// A word as shimen's lexicon defines it. Kavita keys it on (lemma, pos, isProper), the same key shimen uses
/// </summary>
public sealed record SidecarWordDto
{
    [JsonPropertyName("lemma")]
    public string Lemma { get; init; } = string.Empty;
    [JsonPropertyName("pos")]
    public string Pos { get; init; } = string.Empty;
    [JsonPropertyName("isProper")]
    public bool IsProper { get; init; }
    /// <summary>
    /// The dictionary's usual reading in hiragana
    /// </summary>
    [JsonPropertyName("reading")]
    public string Reading { get; init; } = string.Empty;
    /// <summary>
    /// JLPT level from 1 (N1) to 5 (N5), or null when the word isn't listed
    /// </summary>
    [JsonPropertyName("jlpt")]
    public int? Jlpt { get; init; }
    /// <summary>
    /// Every reading the dictionary gives the word, in hiragana
    /// </summary>
    [JsonPropertyName("readings")]
    public IList<string> Readings { get; init; } = [];
    [JsonPropertyName("senses")]
    public IList<SenseDto> Senses { get; init; } = [];

    /// <summary>
    /// A word with more than one sense or more than one reading. Only its occurrences get an analysis row
    /// </summary>
    [JsonIgnore]
    public bool IsAmbiguous => Senses.Count > 1 || Readings.Count > 1;
}

/// <summary>
/// One JMdict sense
/// </summary>
/// <param name="Key"><c>"&lt;seq&gt;:&lt;n&gt;"</c>, the JMdict entry's sequence number and the sense's 1-based position in it</param>
public sealed record SenseDto(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("pos")] IList<string> Pos,
    [property: JsonPropertyName("gloss")] IList<string> Gloss);
