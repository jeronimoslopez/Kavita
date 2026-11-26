using System.Collections.Generic;

namespace Kavita.Models.DTOs.Dictionary;
#nullable enable

/// <summary>
/// A tap on a token, identified by where it is
/// </summary>
/// <param name="Page">0-based EPUB page, as the book reader counts them</param>
/// <param name="Block">The tapped block's <c>data-kb</c> value</param>
/// <param name="S">Start offset of the tapped top-level token in the block's text</param>
/// <param name="Sub">Index into that token's <see cref="TokenDto.Sub"/> to look up one part of a compound, or null for
/// the whole token</param>
/// <param name="Phrase">A <see cref="DictionaryPhraseDto.Index"/> from an earlier answer's
/// <see cref="DictionaryLookupResultDto.PartOf"/>, to look up that phrase instead of the token. <paramref name="S"/> must
/// still fall inside the phrase, so an index from before the page was tokenized again misses. Null, or left out, for a
/// token lookup</param>
public sealed record DictionaryLookupRequestDto(int ChapterId, int Page, int Block, int S, int? Sub, int? Phrase = null);

public sealed record DictionaryLookupResultDto
{
    /// <summary>
    /// The looked-up word. With <see cref="DictionaryLookupRequestDto.Sub"/> set, the part's word, and with
    /// <see cref="DictionaryLookupRequestDto.Phrase"/> set, the phrase's word with no occurrence reading
    /// </summary>
    public DictionaryWordDto Word { get; init; } = null!;
    public IList<SenseDto> Senses { get; init; } = [];
    /// <summary>
    /// The AI answer for this occurrence, or null when there isn't one. Always null for a part of a compound and for a
    /// phrase
    /// </summary>
    public DictionaryAnalysisDto? Analysis { get; init; }
    /// <summary>
    /// The parts of the top-level token at <see cref="DictionaryLookupRequestDto.S"/>, also when a part is the one looked
    /// up, so the popup keeps its part chips. Empty when the token isn't a compound, and for a phrase lookup
    /// </summary>
    public IList<DictionaryPartDto> Parts { get; init; } = [];
    /// <summary>
    /// The block's phrases whose span covers the top-level token at <see cref="DictionaryLookupRequestDto.S"/>, in the
    /// order the block stores them. Empty for a phrase lookup
    /// </summary>
    public IList<DictionaryPhraseDto> PartOf { get; init; } = [];
}

/// <param name="Surface">The text of this occurrence as it appears in the block</param>
/// <param name="OccurrenceReading">The tokenizer's hiragana reading of this occurrence, or null</param>
/// <param name="Reading">The dictionary's usual reading</param>
/// <param name="Readings">Every reading the dictionary gives the word</param>
/// <param name="Jlpt">1 (N1) to 5 (N5), or null when the word isn't listed</param>
public sealed record DictionaryWordDto(long Id, string Surface, string Lemma, string Pos, bool IsProper,
    string? OccurrenceReading, string Reading, IList<string> Readings, int? Jlpt);

/// <param name="SenseKey">Key of the sense that fits this occurrence, or null when none of them fit</param>
/// <param name="Reading">The reading that fits this occurrence, or null when the model gave none</param>
/// <param name="Generator">The model that answered, such as <c>claude-sonnet</c>, for the popup's AI disclosure</param>
public sealed record DictionaryAnalysisDto(string? SenseKey, string? Reading, string Generator);

/// <param name="Sub">The part's index in the token's <see cref="TokenDto.Sub"/>, to send back as
/// <see cref="DictionaryLookupRequestDto.Sub"/></param>
public sealed record DictionaryPartDto(int Sub, long WordId, string Surface, string Lemma, string Reading,
    IList<SenseDto> Senses);

/// <summary>
/// A dictionary expression the looked-up token is part of, such as 気がする for 気
/// </summary>
/// <param name="Index">The phrase's place in its block, to send back as <see cref="DictionaryLookupRequestDto.Phrase"/></param>
/// <param name="S">UTF-16 offset of the phrase's first character in the block's text</param>
/// <param name="E">UTF-16 offset just past its last character</param>
/// <param name="Reading">The dictionary's usual reading</param>
/// <param name="Gloss">The glosses of the word's first sense joined with "; ", or empty when it has no senses</param>
public sealed record DictionaryPhraseDto(int Index, int S, int E, string Lemma, string Reading, string Gloss);
