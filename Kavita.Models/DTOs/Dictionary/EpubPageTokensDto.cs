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
    /// The chapter's series, which saved reading choices belong to
    /// </summary>
    public int SeriesId { get; init; }
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
/// prints it, and <c>[s, e, reading, group]</c> for the spans of a <c>ruby</c> element that has two or more, as
/// <see cref="RubyDto.Group"/> says. Null, and left out of the JSON, when the block has none</param>
/// <param name="Blind">The start offsets of the top-level tokens whose reading the tokenizer chose by fixed cost alone.
/// Null, and left out of the JSON, when the block has none</param>
public sealed record EpubPageBlockDto(int Index, int Length, IList<TokenDto> Tokens,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IList<PhraseDto>? Phrases = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IList<RubyDto>? Ruby = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IList<int>? Blind = null);

/// <summary>
/// A word with everything a lookup returns about it, so the reader can answer a tap without asking the server
/// </summary>
/// <param name="Jlpt">1 (N1) to 5 (N5), or null when the word isn't listed</param>
/// <param name="IsProper">Left out of the JSON when false</param>
/// <param name="Readings">Every reading the dictionary gives the word</param>
/// <param name="Senses">Every sense, each with the readings it's restricted to when it's restricted</param>
/// <param name="Entries">The JMdict entries the senses come from. Null, and left out of the JSON, for a word stored
/// with none</param>
/// <param name="Hint">The reading the book or the series gives the word through its furigana elsewhere. Null, and left
/// out of the JSON, when neither gives one</param>
/// <param name="Choice">The reading the requesting user saved for the word in this series. Null, and left out of the
/// JSON, when there's none</param>
public sealed record EpubPageWordDto(string Lemma, string Reading, int? Jlpt, string Pos,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IsProper,
    IList<string> Readings, IList<SenseDto> Senses,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IList<WordEntryDto>? Entries = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReadingHintDto? Hint = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Choice = null);

/// <summary>
/// The AI answer for one occurrence, found by its block and start offset
/// </summary>
/// <param name="SenseKey">Null when none of the offered senses fit</param>
/// <param name="Reading">Null when the model gave no reading for this occurrence</param>
/// <param name="Generator">The model that answered, as <see cref="DictionaryAnalysisDto.Generator"/> gives it</param>
public sealed record AnalysisEntryDto(int Block, int S, string? SenseKey, string? Reading, string Generator);
