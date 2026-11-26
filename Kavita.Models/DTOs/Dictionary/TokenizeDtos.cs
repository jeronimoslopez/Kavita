using System;
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
    /// <c>sudachipy=..;sudachidict_full=..;modes=C/A;words=6;lexicon=..;sidecar=2</c>
    /// </summary>
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;
    /// <summary>
    /// Every word the tokens and phrases point at. A token's <see cref="TokenDto.W"/> and a phrase's
    /// <see cref="PhraseDto.W"/> are indices into this list
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
    /// <summary>
    /// The block's dictionary expressions, each over two or more whole tokens. A version 1 sidecar sends none, and the
    /// list is empty then
    /// </summary>
    [JsonPropertyName("phrases")]
    public IList<PhraseDto> Phrases { get; init; } = [];
    /// <summary>
    /// The start offsets of the top-level tokens whose reading the tokenizer chose by fixed cost alone, so the sentence
    /// had no say in it. A sidecar before version 3 sends none, and the list is empty then
    /// </summary>
    [JsonPropertyName("blind")]
    public IList<int> Blind { get; init; } = [];
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
    /// The JMdict entries the senses come from, in the order their senses appear. Null when the answer has no entries
    /// field, as a sidecar before version 3 sends it, and empty for a version 3 word no entry matched
    /// </summary>
    [JsonPropertyName("entries")]
    public IList<WordEntryDto>? Entries { get; init; }

    /// <summary>
    /// A word with more than one sense or more than one reading. Only its occurrences get an analysis row. The reading
    /// store applies the same rule to the stored word's JSON arrays, and its tests check the two agree
    /// </summary>
    [JsonIgnore]
    public bool IsAmbiguous => Senses.Count > 1 || Readings.Count > 1;
}

/// <summary>
/// One JMdict sense
/// </summary>
/// <param name="Key"><c>"&lt;seq&gt;:&lt;n&gt;"</c>, the JMdict entry's sequence number and the sense's 1-based position in it</param>
/// <param name="R">The readings of its entry the sense is restricted to, in hiragana. Null, and left out of the JSON,
/// when the sense takes every reading of its entry</param>
public sealed record SenseDto(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("pos")] IList<string> Pos,
    [property: JsonPropertyName("gloss")] IList<string> Gloss,
    [property: JsonPropertyName("r"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IList<string>? R = null)
{
    /// <summary>
    /// The JMdict seq of the sense's entry, the part of <see cref="Key"/> before the colon, or null when the key has
    /// no number there
    /// </summary>
    [JsonIgnore]
    public long? Seq => Key.IndexOf(':') is > 0 and var colon && long.TryParse(Key.AsSpan(0, colon), out var seq) ? seq : null;
}

/// <summary>
/// One JMdict entry a word's senses come from
/// </summary>
/// <param name="Seq">The JMdict seq, the part of a sense key before the colon</param>
/// <param name="Common">Whether the spelling is a common form in the entry</param>
/// <param name="Readings">The entry's kana forms that read the spelling, in hiragana and JMdict's order</param>
public sealed record WordEntryDto(
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("common")] bool Common,
    [property: JsonPropertyName("readings")] IList<string> Readings);
