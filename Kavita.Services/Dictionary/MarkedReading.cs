using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Kavita.Models.DTOs.Dictionary;

namespace Kavita.Services.Dictionary;

/// <summary>
/// The reading a book's furigana gives one token
/// </summary>
/// <param name="Printed">The surface's reading as the book prints it, each covered base replaced by its ruby and every
/// other character as written</param>
/// <param name="Reading"><paramref name="Printed"/> in hiragana</param>
/// <param name="DictionaryPrinted">The reading of the token's lemma in the book's script, or null when the surface's
/// stem doesn't match the lemma's</param>
/// <param name="DictionaryReading"><paramref name="DictionaryPrinted"/> in hiragana, or null with it</param>
public sealed record MarkedReadingResult(string Printed, string Reading, string? DictionaryPrinted,
    string? DictionaryReading);

/// <summary>
/// Reads a token the way its book's furigana marks it
/// </summary>
public static class MarkedReading
{
    /// <summary>
    /// The token's reading from the ruby spans over it, when the book marks every kanji of the token
    /// </summary>
    /// <remarks>
    /// <para>
    /// A token is marked when at least one span lies inside it, every kanji of its surface is under a span, and no
    /// span crosses its start or its end. Kanji are the CJK unified ideographs with their extensions, the CJK
    /// compatibility ideographs, and 々, 〆 and 〇. A span whose reading isn't all kana, such as emphasis dots
    /// printed as ruby, counts as no span at all. Spans that overlap each other, as nested ruby gives, leave the
    /// token unmarked.
    /// </para>
    /// <para>
    /// The dictionary form takes the surface's reading and swaps the kana after its stem for the lemma's, so 開か read
    /// あか with the lemma 開く gives あく. The stem is the surface without its trailing kana, which can't sit under a
    /// span. It must be the lemma's stem character for character, and not empty, or the dictionary form is null.
    /// </para>
    /// </remarks>
    /// <param name="text">The block's text</param>
    /// <param name="token">One top-level token of the block</param>
    /// <param name="ruby">The block's ruby spans, in any order</param>
    /// <param name="lemma">The lemma of the token's word, or null when it has none</param>
    /// <returns>Null when the token isn't marked</returns>
    public static MarkedReadingResult? Of(string text, TokenDto token, IReadOnlyList<RubyDto> ruby, string? lemma)
    {
        if (token.S < 0 || token.E > text.Length || token.E <= token.S) return null;

        var inside = new List<RubyDto>();
        foreach (var span in ruby)
        {
            if (span.E <= token.S || span.S >= token.E || !IsKanaReading(span.Reading)) continue;
            if (span.S < token.S || span.E > token.E) return null;
            inside.Add(span);
        }
        if (inside.Count == 0) return null;
        inside.Sort((a, b) => a.S.CompareTo(b.S));

        var printed = new StringBuilder();
        var position = token.S;
        var next = 0;
        while (position < token.E)
        {
            if (next < inside.Count && inside[next].S < position) return null;
            if (next < inside.Count && inside[next].S == position)
            {
                printed.Append(inside[next].Reading);
                position = inside[next].E;
                next++;
                continue;
            }

            var length = position + 1 < token.E && char.IsSurrogatePair(text, position) ? 2 : 1;
            var codePoint = length == 2 ? char.ConvertToUtf32(text[position], text[position + 1]) : text[position];
            if (IsKanji(codePoint)) return null;
            printed.Append(text, position, length);
            position += length;
        }

        var surface = text[token.S..token.E];
        var printedText = printed.ToString();
        var dictionary = DictionaryForm(surface, printedText, inside[^1].E - token.S, lemma);
        return new MarkedReadingResult(printedText, ToHiragana(printedText), dictionary,
            dictionary == null ? null : ToHiragana(dictionary));
    }

    /// <param name="coveredEnd">Where the last span ends, as an offset into <paramref name="surface"/></param>
    private static string? DictionaryForm(string surface, string printed, int coveredEnd, string? lemma)
    {
        if (string.IsNullOrEmpty(lemma)) return null;
        if (lemma == surface) return printed;

        var surfaceStem = surface.Length;
        while (surfaceStem > coveredEnd && IsKana(surface[surfaceStem - 1])) surfaceStem--;
        var lemmaStem = lemma.Length;
        while (lemmaStem > 0 && IsKana(lemma[lemmaStem - 1])) lemmaStem--;

        if (surfaceStem == 0 || !surface.AsSpan(0, surfaceStem).SequenceEqual(lemma.AsSpan(0, lemmaStem))) return null;

        // The trailing kana aren't under a span, so the printed reading ends with them as written
        return printed[..^(surface.Length - surfaceStem)] + lemma[lemmaStem..];
    }

    /// <summary>
    /// Katakana turned into the hiragana for the same sound. The long vowel mark and other characters stay as they are
    /// </summary>
    public static string ToHiragana(string value)
    {
        if (!value.Any(c => c is >= 'ァ' and <= 'ヶ' or 'ヽ' or 'ヾ')) return value;

        var result = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            result.Append(c switch
            {
                >= 'ァ' and <= 'ヶ' => (char) (c - 0x60),
                'ヽ' => 'ゝ',
                'ヾ' => 'ゞ',
                _ => c,
            });
        }
        return result.ToString();
    }

    public static bool IsKanji(int codePoint) => codePoint is
        (>= 0x3400 and <= 0x4DBF) or (>= 0x4E00 and <= 0x9FFF) or (>= 0xF900 and <= 0xFAFF) or
        (>= 0x20000 and <= 0x323AF) or 0x3005 or 0x3006 or 0x3007;

    /// <summary>
    /// Hiragana, katakana and the long vowel mark, without the katakana middle dot
    /// </summary>
    private static bool IsKana(char c) => c is (>= 'ぁ' and <= 'ゖ') or (>= '゙' and <= 'ゟ') or
        (>= 'ァ' and <= 'ヺ') or (>= 'ー' and <= 'ヿ') or (>= 'ㇰ' and <= 'ㇿ');

    private static bool IsKanaReading(string reading) => reading.Length > 0 && reading.All(IsKana);
}
