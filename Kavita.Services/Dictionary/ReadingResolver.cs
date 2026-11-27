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
/// <param name="Bound">The occurrence is a suffix or follows a numeral, where the tokenizer's reading is the counter's
/// or the suffix's and often sits in a group JMdict doesn't mark common</param>
public sealed record ReadingInputs(IReadOnlyList<ReadingGroupDto>? Groups, MarkedReadingResult? Marked, string? Choice,
    ReadingHintDto? Hint, string? TokenReading, bool Blind, bool Bound = false);

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
        ReadingGroupDto? first = null;
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
            // A blind reading is no evidence. A group far more frequent than the rest goes first, and without one a
            // rare group holding the reading gives way to the first common group. A bound occurrence keeps it, since
            // 二本 read ぽん or 山 read さん as a suffix is the reading JMdict files as rare
            var tokenGroup = groups.First(g => g.Readings.Contains(inputs.TokenReading!));
            var frequent = inputs.Blind && !inputs.Bound ? FrequencyPick(groups) : null;
            var common = groups.FirstOrDefault(g => g.Common);
            if (frequent != null && !ReferenceEquals(frequent, tokenGroup))
            {
                (pick, source, level) = (frequent.Readings[0], ReadingSource.Dictionary, ReadingLevel.Guess);
                first = frequent;
            }
            else if (frequent == null && inputs.Blind && !inputs.Bound && common != null && !tokenGroup.Common)
            {
                (pick, source, level) = (common.Readings[0], ReadingSource.Dictionary, ReadingLevel.Guess);
            }
            else
            {
                (pick, source, level) = (inputs.TokenReading!, ReadingSource.Tokenizer,
                    inputs.Blind ? ReadingLevel.Guess : ReadingLevel.Likely);
            }
        }
        else if (inputs.Bound && inputs.TokenReading is { } voiced && UnvoiceFirst(voiced) is var plain
                 && plain != voiced && ReadingGroups.AnyHas(groups, plain))
        {
            // A counter's first sound voices after some numbers, as 二杯 read ばい or 一本 read ぽん, and JMdict lists
            // only the plain はい or ほん
            (pick, source, level) = (plain, ReadingSource.Tokenizer, inputs.Blind ? ReadingLevel.Guess : ReadingLevel.Likely);
        }
        else
        {
            first = FrequencyPick(groups) ?? groups[0];
            (pick, source, level) = (first.Readings[0], ReadingSource.Dictionary, ReadingLevel.Guess);
        }

        first ??= groups.First(g => g.Readings.Contains(pick));
        return new ReadingsDto(level, source, null, pick, false,
            groups.Where(g => !ReferenceEquals(g, first)).Prepend(first).ToList());
    }

    /// <summary>
    /// How many times the second most frequent group's rank must be the first's for the first to be the
    /// <see cref="FrequencyPick"/>
    /// </summary>
    public const int FrequencyFactor = 5;

    /// <summary>
    /// The group frequency clearly favours. Of the groups with a rank, taken by rank from the lowest, the first is the
    /// pick when it's the only one or when the second's rank is at least <see cref="FrequencyFactor"/> times its own.
    /// The reader's frequencyPick is the same rule
    /// </summary>
    /// <returns>Null when no group has a rank or the two lowest are too close</returns>
    public static ReadingGroupDto? FrequencyPick(IReadOnlyList<ReadingGroupDto> groups)
    {
        var ranked = groups.Where(g => g.Rank != null).OrderBy(g => g.Rank!.Value).Take(2).ToList();
        return ranked.Count switch
        {
            0 => null,
            1 => ranked[0],
            _ => (long) ranked[1].Rank!.Value >= (long) ranked[0].Rank!.Value * FrequencyFactor ? ranked[0] : null,
        };
    }

    private const string VoicedKana = "がぎぐげござじずぜぞだぢづでどばびぶべぼぱぴぷぺぽ";
    private const string PlainKana = "かきくけこさしすせそたちつてとはひふへほはひふへほ";

    /// <returns>The hiragana reading with its first kana's dakuten or handakuten taken off, so ばい gives はい and ぽん
    /// gives ほん. A reading whose first kana is already plain, or an empty one, comes back as it is</returns>
    public static string UnvoiceFirst(string reading)
    {
        if (reading.Length == 0) return reading;
        var at = VoicedKana.IndexOf(reading[0]);
        return at < 0 ? reading : PlainKana[at] + reading[1..];
    }

    /// <summary>
    /// The inputs for a top-level token, read from its block
    /// </summary>
    /// <param name="choice">The user's saved reading for the token's word in this series, or null</param>
    /// <param name="hint">The word's hint, or null</param>
    public static ReadingInputs ForToken(StoredBlock block, TokenDto token, StoredWord word, string? choice,
        ReadingHintDto? hint) =>
        new(GroupsOf(word), MarkedReading.Of(block.Text, token, block.Ruby, word.Lemma), choice, hint,
            TokenReading(block.Text, token.S, token.E, token.R, word), block.Blind.Contains(token.S),
            IsBound(block.Text, token.S, word));

    /// <summary>
    /// The tokenizer's part of speech for a suffix, such as 家 read か after a surname
    /// </summary>
    public const string SuffixPos = "接尾辞";

    /// <summary>
    /// The characters a counter follows, besides the digits
    /// </summary>
    private const string NumeralKanji = "〇一二三四五六七八九十百千万億兆数何幾";

    /// <returns>True when the word is a suffix or the character before the occurrence is a numeral</returns>
    public static bool IsBound(string text, int s, StoredWord word) =>
        word.Pos == SuffixPos || (s > 0 && s <= text.Length && IsNumeral(text[s - 1]));

    /// <returns>True for an ASCII or full-width digit, or a kanji that a counter follows, such as 二, 百 or 何</returns>
    public static bool IsNumeral(char ch) =>
        ch is >= '0' and <= '9' or >= '０' and <= '９' || NumeralKanji.Contains(ch);

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
