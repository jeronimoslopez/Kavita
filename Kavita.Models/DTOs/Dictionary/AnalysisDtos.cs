using System.Collections.Generic;

namespace Kavita.Models.DTOs.Dictionary;
#nullable enable

/// <summary>
/// A stretch of a book's text, about 2,000 characters, with the ambiguous occurrences in it that still need an answer
/// </summary>
/// <param name="Text">Whole blocks from one EPUB page, joined by <c>\n</c></param>
/// <param name="Items">Ordered by their offset in <paramref name="Text"/></param>
public sealed record AnalysisPassageDto(string Text, IList<AnalysisItemDto> Items);

/// <param name="Id">The analysis row to answer, sent back as <see cref="AnalysisAnswerDto.Id"/></param>
/// <param name="S">UTF-16 offset of the occurrence in the passage's text</param>
/// <param name="E">UTF-16 offset just past the occurrence</param>
/// <param name="Readings">The only readings an answer may give</param>
/// <param name="Senses">The only senses an answer may pick</param>
public sealed record AnalysisItemDto(long Id, int S, int E, string Surface, string Lemma, IList<string> Readings,
    IList<SenseDto> Senses);

/// <param name="Sense">One of the item's sense keys, or null when none of them fit</param>
/// <param name="Reading">One of the item's readings, or null to give none</param>
public sealed record AnalysisAnswerDto(long Id, string? Sense, string? Reading);

public sealed record AnalysisStoreRequestDto
{
    /// <summary>
    /// The model that answered, such as <c>claude-sonnet</c>. Stored with each answer
    /// </summary>
    public string Generator { get; init; } = string.Empty;
    /// <summary>
    /// The runner's prompt version, so answers from an older prompt can be found later
    /// </summary>
    public string PromptVersion { get; init; } = string.Empty;
    public IList<AnalysisAnswerDto> Items { get; init; } = [];
}

/// <param name="Stored">How many answers were saved</param>
/// <param name="Rejected">Every answer that wasn't saved. The others in the same request are saved anyway</param>
public sealed record AnalysisStoreResultDto(int Stored, IList<AnalysisRejectionDto> Rejected);

/// <param name="Reason">Why the answer was refused, in plain words for the runner's log, such as
/// "sense 1234:9 was not offered"</param>
public sealed record AnalysisRejectionDto(long Id, string Reason);

/// <summary>
/// Body of the whole-book analysis endpoints
/// </summary>
public sealed record DictionarySeriesRequestDto
{
    public int SeriesId { get; init; }
}
