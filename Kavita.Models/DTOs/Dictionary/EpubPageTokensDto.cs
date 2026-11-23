using System.Collections.Generic;

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
    /// Every word a token on this page points at, keyed by reading.db word id
    /// </summary>
    public IDictionary<long, WordBriefDto> Words { get; init; } = new Dictionary<long, WordBriefDto>();
    /// <summary>
    /// The stored AI answers for this page's occurrences. Occurrences without an answer have no entry
    /// </summary>
    public IList<AnalysisEntryDto> Analysis { get; init; } = [];
}

/// <param name="Index">The block's <c>data-kb</c> value</param>
/// <param name="Length">UTF-16 length of the block's text. The reader compares it with the text it rebuilds from the
/// DOM, and skips lookups in the block when they differ</param>
/// <param name="Tokens">Word ids in <see cref="TokenDto.W"/> are reading.db word ids</param>
public sealed record EpubPageBlockDto(int Index, int Length, IList<TokenDto> Tokens);

/// <summary>
/// What the reader shows before a word is looked up, such as a JLPT underline or furigana
/// </summary>
/// <param name="Jlpt">1 (N1) to 5 (N5), or null when the word isn't listed</param>
public sealed record WordBriefDto(string Lemma, string Reading, int? Jlpt);

/// <summary>
/// The AI answer for one occurrence, found by its block and start offset
/// </summary>
/// <param name="SenseKey">Null when none of the offered senses fit</param>
/// <param name="Reading">Null when the model gave no reading for this occurrence</param>
public sealed record AnalysisEntryDto(int Block, int S, string? SenseKey, string? Reading);
