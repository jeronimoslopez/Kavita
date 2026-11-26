using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Kavita.Models.DTOs.Dictionary;
#nullable enable

/// <summary>
/// Everything the book reader needs to find words on one EPUB page without asking again per tap
/// </summary>
public sealed record EpubPageTokensDto
{
    /// <summary>
    /// The tokenizer version the page was tokenized with. It changes when the page is tokenized again, so the reader
    /// can cache by it
    /// </summary>
    public string Version { get; init; } = string.Empty;
    /// <summary>
    /// Blocks in document order. A block's <see cref="EpubPageBlockDto.Index"/> is the <c>data-kb</c> value on its element
    /// </summary>
    public IList<EpubPageBlockDto> Blocks { get; init; } = [];
    /// <summary>
    /// Every word a lookup on this page can return, keyed by reading.db word id. That's the words of the tokens, of
    /// their parts and of the blocks' phrases
    /// </summary>
    public IDictionary<long, EpubPageWordDto> Words { get; init; } = new Dictionary<long, EpubPageWordDto>();
    /// <summary>
    /// The stored AI answers for this page's occurrences. Occurrences without an answer have no entry
    /// </summary>
    public IList<AnalysisEntryDto> Analysis { get; init; } = [];
}

/// <param name="Index">The block's <c>data-kb</c> value</param>
/// <param name="Length">UTF-16 length of the block's text. The reader compares it with the text it rebuilds from the
/// DOM, and skips lookups in the block when they differ</param>
/// <param name="Tokens">Word ids in <see cref="TokenDto.W"/> are reading.db word ids</param>
/// <param name="Phrases">The block's phrases in stored order, so a phrase's position is the
/// <see cref="DictionaryLookupRequestDto.Phrase"/> index that looks it up. Word ids are reading.db word ids. Null, and
/// left out of the JSON, when the block has none</param>
/// <param name="Ruby">The block's furigana as <c>[s, e, reading]</c> spans over its text, with the reading as the book
/// prints it. Null, and left out of the JSON, when the block has none</param>
public sealed record EpubPageBlockDto(int Index, int Length, IList<TokenDto> Tokens,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IList<PhraseDto>? Phrases = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IList<RubyDto>? Ruby = null);

/// <summary>
/// A word with everything a lookup returns about it, so the reader can answer a tap without asking the server
/// </summary>
/// <param name="Jlpt">1 (N1) to 5 (N5), or null when the word isn't listed</param>
/// <param name="IsProper">Left out of the JSON when false</param>
/// <param name="Readings">Every reading the dictionary gives the word</param>
public sealed record EpubPageWordDto(string Lemma, string Reading, int? Jlpt, string Pos,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IsProper,
    IList<string> Readings, IList<SenseDto> Senses);

/// <summary>
/// The AI answer for one occurrence, found by its block and start offset
/// </summary>
/// <param name="SenseKey">Null when none of the offered senses fit</param>
/// <param name="Reading">Null when the model gave no reading for this occurrence</param>
/// <param name="Generator">The model that answered, as <see cref="DictionaryAnalysisDto.Generator"/> gives it</param>
public sealed record AnalysisEntryDto(int Block, int S, string? SenseKey, string? Reading, string Generator);
