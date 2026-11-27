using System.Collections.Generic;
using System.Linq;
using Kavita.Models.DTOs.Dictionary;

namespace Kavita.Services.Dictionary;

/// <summary>
/// What the resolver knows about one lookup
/// </summary>
/// <param name="Groups">The word's groups, from <see cref="ReadingGroups"/>, or null for a word stored without
/// entries, which always resolves to <see cref="ReadingsDto.None"/></param>
/// <param name="Marked">How the book's furigana reads this occurrence, or null when it doesn't mark it</param>
/// <param name="Choice">The reading the user saved for the word in this series, or null</param>
/// <param name="Hint">The reading the book or the series gives the word elsewhere, or null</param>
/// <param name="TokenReading">The tokenizer's reading of the occurrence in dictionary form, or the word's reading when
/// that can't be had</param>
/// <param name="Blind">The tokenizer chose the occurrence's reading by fixed cost alone</param>
public sealed record ReadingInputs(IReadOnlyList<ReadingGroupDto>? Groups, MarkedReadingResult? Marked, string? Choice,
    ReadingHintDto? Hint, string? TokenReading, bool Blind);

/// <summary>
/// Orders a word's reading groups for one lookup and says how sure the first one is. The reader runs a TypeScript
/// twin of this on its own lookups, and the parity fixture checks the two agree
/// </summary>
public static class ReadingResolver
{
    public static ReadingsDto Resolve(ReadingInputs inputs)
    {
        if (inputs.Groups is not { } groups) return ReadingsDto.None;

        if (inputs.Marked is { } marked)
        {
            // The dictionary-form pair when there is one, so 開か marked あ reads あく
            var reading = marked.DictionaryReading ?? marked.Reading;
            var printed = marked.DictionaryReading != null ? marked.DictionaryPrinted! : marked.Printed;
            var match = groups.FirstOrDefault(g => g.Readings.Contains(reading));
            return match != null
                ? new ReadingsDto(ReadingLevel.Certain, ReadingSource.Furigana, printed, reading, false, [match])
                : new ReadingsDto(ReadingLevel.Certain, ReadingSource.Furigana, printed, null, true, []);
        }

        if (groups.Count == 0)
        {
            return inputs.Hint is { } hint
                ? new ReadingsDto(ReadingLevel.Likely, hint.Source, hint.Printed, null, false, [])
                : ReadingsDto.None;
        }

        if (groups.Count == 1)
        {
            var only = groups[0];
            var tokenPick = inputs.TokenReading != null && only.Readings.Contains(inputs.TokenReading) ? inputs.TokenReading : null;
            return new ReadingsDto(ReadingLevel.Certain, ReadingSource.Dictionary, null, tokenPick, false, [only]);
        }

        string pick, source, level;
        if (ReadingGroups.AnyHas(groups, inputs.Choice))
        {
            (pick, source, level) = (inputs.Choice!, ReadingSource.Choice, ReadingLevel.Likely);
        }
        else if (inputs.Hint is { } hint && ReadingGroups.AnyHas(groups, hint.Reading))
        {
            (pick, source, level) = (hint.Reading, hint.Source, ReadingLevel.Likely);
        }
        else if (ReadingGroups.AnyHas(groups, inputs.TokenReading))
        {
            // A blind reading is no evidence, so a rare group holding it gives way to the first common group
            var common = groups.FirstOrDefault(g => g.Common);
            var blindRare = inputs.Blind && common != null
                && !groups.First(g => g.Readings.Contains(inputs.TokenReading!)).Common;
            (pick, source, level) = blindRare
                ? (common!.Readings[0], ReadingSource.Dictionary, ReadingLevel.Guess)
                : (inputs.TokenReading!, ReadingSource.Tokenizer, inputs.Blind ? ReadingLevel.Guess : ReadingLevel.Likely);
        }
        else
        {
            (pick, source, level) = (groups[0].Readings[0], ReadingSource.Dictionary, ReadingLevel.Guess);
        }

        var first = groups.First(g => g.Readings.Contains(pick));
        return new ReadingsDto(level, source, null, pick, false,
            groups.Where(g => !ReferenceEquals(g, first)).Prepend(first).ToList());
    }

    /// <summary>
    /// The inputs for a top-level token, read from its block
    /// </summary>
    /// <param name="choice">The user's saved reading for the token's word in this series, or null</param>
    /// <param name="hint">The word's hint, or null</param>
    public static ReadingInputs ForToken(StoredBlock block, TokenDto token, StoredWord word, string? choice,
        ReadingHintDto? hint) =>
        new(GroupsOf(word), MarkedReading.Of(block.Text, token, block.Ruby, word.Lemma), choice, hint,
            TokenReading(block.Text, token.S, token.E, token.R, word), block.Blind.Contains(token.S));

    /// <summary>
    /// The inputs for one part of a compound, from the part's own text, reading and lemma. A part has no furigana,
    /// saved choice, hint or blind flag of its own
    /// </summary>
    public static ReadingInputs ForPart(StoredBlock block, SubTokenDto part, StoredWord word) =>
        new(GroupsOf(word), null, null, null, TokenReading(block.Text, part.S, part.E, part.R, word), false);

    /// <summary>
    /// The inputs for a phrase, which has no occurrence reading, so the word's reading stands in
    /// </summary>
    public static ReadingInputs ForPhrase(StoredWord word) => new(GroupsOf(word), null, null, null, word.Reading, false);

    /// <returns>The occurrence's reading with the surface's trailing kana swapped for the lemma's, or the word's
    /// reading when the occurrence has none or the swap can't be made</returns>
    public static string TokenReading(string text, int s, int e, string? reading, StoredWord word)
    {
        if (reading == null || s < 0 || e > text.Length || e <= s) return word.Reading;
        return MarkedReading.DictionaryReading(text[s..e], reading, word.Lemma) ?? word.Reading;
    }

    /// <returns>Null for a word stored without entries, so it never reaches the resolver's rules</returns>
    private static IReadOnlyList<ReadingGroupDto>? GroupsOf(StoredWord word) =>
        word.Entries == null ? null : ReadingGroups.Of(word);
}
