using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Kavita.Models.DTOs.Dictionary;
#nullable enable

/// <summary>
/// The senses of a word the popup shows under one reading tab. A group is one JMdict entry, or the part of one whose
/// readings share the same senses when the entry restricts some senses by reading, with every such part that has the
/// same readings list folded into the first. A word whose lemma has no kanji has one group holding all its senses
/// </summary>
/// <param name="Seq">The JMdict seq of the group's first entry</param>
/// <param name="Readings">The group's readings in hiragana, in the entry's order. For a word whose lemma has no
/// kanji, the lemma in hiragana alone</param>
/// <param name="Common">True when the <see cref="WordEntryDto.Common"/> of any entry in the group is</param>
/// <param name="Senses">The senses that apply to these readings, in the word's order</param>
public sealed record ReadingGroupDto(long Seq, IList<string> Readings, bool Common, IList<SenseDto> Senses);

/// <summary>
/// Which reading the popup opens on, and how sure that is
/// </summary>
/// <param name="Level">One of <see cref="ReadingLevel"/>'s values</param>
/// <param name="Source">One of <see cref="ReadingSource"/>'s values, where the first group came from</param>
/// <param name="Shown">A reading to show in the headword in place of the first group's, when the book prints one the
/// dictionary doesn't have or prints a name in katakana. Null otherwise</param>
/// <param name="Pick">The hiragana reading that put the first group first, which the headword shows when
/// <paramref name="Shown"/> is null. Null when nothing picked one</param>
/// <param name="BookOnly">The book's furigana gives a reading no group has, so the popup shows every sense ungrouped</param>
/// <param name="Groups">The word's groups, the best one first and the rest in their original order. Empty for a word
/// with no entries, which the popup shows the way it showed every word before groups</param>
/// <remarks>Every field is written, nulls included, because the reader's twin compares the whole object</remarks>
public sealed record ReadingsDto(string Level, string Source, string? Shown, string? Pick, bool BookOnly,
    IList<ReadingGroupDto> Groups)
{
    /// <summary>
    /// What a word stored without entries always resolves to, and a word with no groups and nothing else to go on
    /// </summary>
    public static ReadingsDto None => new(ReadingLevel.Certain, ReadingSource.Dictionary, null, null, false, []);
}

/// <summary>
/// A reading the book or the series gives a word through its furigana elsewhere
/// </summary>
/// <param name="Reading">The reading in hiragana</param>
/// <param name="Printed">The reading as the book prints it most often</param>
/// <param name="Source"><see cref="ReadingSource.Book"/> or <see cref="ReadingSource.Series"/></param>
public sealed record ReadingHintDto(
    [property: JsonPropertyName("reading")] string Reading,
    [property: JsonPropertyName("printed")] string Printed,
    [property: JsonPropertyName("source")] string Source);

/// <summary>
/// How sure the resolver is of the first group, as the wire spells it
/// </summary>
public static class ReadingLevel
{
    public const string Certain = "certain";
    public const string Likely = "likely";
    public const string Guess = "guess";
}

/// <summary>
/// Where the first group came from, as the wire spells it
/// </summary>
public static class ReadingSource
{
    public const string Furigana = "furigana";
    public const string Choice = "choice";
    public const string Book = "book";
    public const string Series = "series";
    public const string Tokenizer = "tokenizer";
    public const string Dictionary = "dictionary";
}

/// <summary>
/// <c>PUT api/dictionary/reading-choice</c>
/// </summary>
/// <param name="Reading">A reading of one of the word's groups, in hiragana</param>
public sealed record ReadingChoiceDto(int SeriesId, long WordId, string Reading);
