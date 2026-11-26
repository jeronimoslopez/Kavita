using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Kavita.Models.DTOs.Dictionary;

namespace Kavita.Services.Dictionary;

/// <summary>
/// A word one furigana span of a book defines, such as 翠苓 under スイレイ, which the tokenizer splits into 翠 and 苓
/// </summary>
/// <param name="Text">The covered text</param>
/// <param name="Reading">The span's reading in hiragana</param>
/// <param name="Printed">The span's reading as the book prints it</param>
public sealed record BookWord(string Text, string Reading, string Printed);

/// <summary>
/// One book_word row of a page
/// </summary>
/// <param name="Reading">In hiragana</param>
/// <param name="Printed">The page's most frequent printed form of that reading</param>
/// <param name="Count">How many of the page's spans define the word with that reading</param>
public sealed record PageBookWord(string Text, string Reading, string Printed, int Count);

/// <summary>
/// Finds the words a block's furigana defines over several of its tokens
/// </summary>
public static class BookWords
{
    /// <summary>
    /// The part of speech of a book word's row in the word table
    /// </summary>
    public const string Pos = "名詞";

    /// <summary>
    /// Stored as the lexicon version of a book word's row, so it reads apart from a sidecar's
    /// </summary>
    public const string LexiconVersion = "book";

    /// <summary>
    /// Counters and units of time, which the single-kanji rule never lets start a book word, so 回目 and 年前 stay
    /// split
    /// </summary>
    public const string CounterKanji = "年月日時分秒回度歳円組目番人本個枚階";

    private const string KanjiNumerals = "〇一二三四五六七八九十百千万億兆";

    /// <summary>
    /// The book words one block defines, one per span that defines one, in the order of the spans
    /// </summary>
    /// <remarks>
    /// <para>
    /// A span defines a book word when it starts at one top-level token's start and ends at a later one's end, so it
    /// covers two or more whole tokens, its reading is all kana, no other span overlaps it, the covered text has a
    /// kanji, and no covered token is all numerals. On top of that, one of these holds:
    /// </para>
    /// <list type="bullet">
    /// <item>The reading isn't the covered tokens' own readings in a row, by <see cref="IsPiecesReading"/>. That's
    /// 翠苓 under スイレイ and 果実水 under ジュース, and not 脱出方法 under だっしゅつほうほう.</item>
    /// <item>Every covered token is one kanji, the first isn't in <see cref="CounterKanji"/>, and the token before the
    /// span, when there is one, isn't all numerals. That's names whose reading happens to be their kanji's, such as
    /// 羅半 under ラハン over 羅 ら and 半 はん.</item>
    /// </list>
    /// </remarks>
    /// <param name="text">The block's text</param>
    /// <param name="tokens">The block's top-level tokens in text order</param>
    /// <param name="ruby">The block's ruby spans, in any order</param>
    public static IReadOnlyList<BookWord> Detect(string text, IReadOnlyList<TokenDto> tokens, IReadOnlyList<RubyDto> ruby)
    {
        var result = new List<BookWord>();
        if (ruby.Count == 0 || tokens.Count < 2) return result;

        var kana = ruby.Where(r => r.S >= 0 && r.E <= text.Length && r.S < r.E && MarkedReading.IsKanaReading(r.Reading))
            .OrderBy(r => r.S).ToList();
        for (var i = 0; i < kana.Count; i++)
        {
            var span = kana[i];
            var overlaps = (i > 0 && kana[i - 1].E > span.S) || (i + 1 < kana.Count && kana[i + 1].S < span.E);
            if (overlaps) continue;

            var first = IndexStartingAt(tokens, span.S);
            if (first < 0) continue;
            var last = first;
            while (last < tokens.Count && tokens[last].E < span.E) last++;
            if (last >= tokens.Count || tokens[last].E != span.E || last == first) continue;

            var covered = tokens.Skip(first).Take(last - first + 1).ToList();
            if (covered.Any(t => t.S < 0 || t.E > text.Length || t.E <= t.S)) continue;
            if (!HasKanji(text, span.S, span.E)) continue;
            if (covered.Any(t => IsNumerals(text, t.S, t.E))) continue;

            var reading = MarkedReading.ToHiragana(span.Reading);
            var pieces = covered.Select(t => t.R ?? text[t.S..t.E]).ToList();
            var defines = !IsPiecesReading(pieces, reading) ||
                          IsSingleKanjiName(text, covered, first == 0 ? null : tokens[first - 1]);
            if (defines) result.Add(new BookWord(text[span.S..span.E], reading, span.Reading));
        }
        return result;
    }

    /// <summary>
    /// The book_word rows one page's blocks give, one per text and reading with how many spans define it there and
    /// the form the page prints most often for it, ties to the first in ordinal order
    /// </summary>
    /// <returns>Ordered by text, then reading</returns>
    public static IReadOnlyList<PageBookWord> ForPage(IEnumerable<StoredBlock> blocks)
    {
        var printed = new Dictionary<(string Text, string Reading), Dictionary<string, int>>();
        foreach (var block in blocks)
        {
            foreach (var word in Detect(block.Text, block.Tokens, block.Ruby))
            {
                if (!printed.TryGetValue((word.Text, word.Reading), out var forms))
                {
                    forms = new Dictionary<string, int>(StringComparer.Ordinal);
                    printed[(word.Text, word.Reading)] = forms;
                }
                forms[word.Printed] = forms.GetValueOrDefault(word.Printed) + 1;
            }
        }

        return printed
            .Select(p => new PageBookWord(p.Key.Text, p.Key.Reading,
                p.Value.OrderByDescending(f => f.Value).ThenBy(f => f.Key, StringComparer.Ordinal).First().Key,
                p.Value.Values.Sum()))
            .OrderBy(w => w.Text, StringComparer.Ordinal).ThenBy(w => w.Reading, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Whether <paramref name="reading"/> is just the pieces' readings in a row. Both sides are compared in hiragana
    /// with small kana as large, so ょ matches よ and っ matches つ. A piece after the first may start voiced where its
    /// own reading doesn't (rendaku, ふか in 興味深 read ぶか), and a piece before the last may end in つ where its own
    /// reading ends in く, つ, ち or き (学 がく in がっこう). Failing that, the two still match when they're equal with
    /// every つ removed
    /// </summary>
    /// <param name="pieces">Each covered token's reading, or its text when it has none</param>
    /// <param name="reading">The span's reading in hiragana</param>
    public static bool IsPiecesReading(IReadOnlyList<string> pieces, string reading)
    {
        var normalised = pieces.Select(Normalise).ToList();
        var marked = Normalise(reading);

        var position = 0;
        var aligned = true;
        for (var i = 0; i < normalised.Count && aligned; i++)
        {
            var piece = normalised[i];
            if (piece.Length == 0 || position + piece.Length > marked.Length)
            {
                aligned = false;
                break;
            }

            for (var k = 0; k < piece.Length; k++)
            {
                var m = marked[position + k];
                var p = piece[k];
                if (m == p) continue;
                if (k == 0 && i > 0 && Unvoiced(m) == Unvoiced(p)) continue;
                if (k == piece.Length - 1 && i < normalised.Count - 1 && m == 'つ' && p is 'く' or 'つ' or 'ち' or 'き') continue;
                aligned = false;
                break;
            }
            position += piece.Length;
        }
        if (aligned && position == marked.Length) return true;

        return Loose(string.Concat(normalised)) == Loose(marked);
    }

    /// <summary>
    /// The second way a span qualifies, for a name of single kanji whose reading is theirs one by one
    /// </summary>
    /// <param name="before">The top-level token just before the span, or null when the span starts the block</param>
    private static bool IsSingleKanjiName(string text, IReadOnlyList<TokenDto> covered, TokenDto? before)
    {
        foreach (var token in covered)
        {
            if (SingleCodePoint(text, token.S, token.E) is not { } codePoint || !MarkedReading.IsKanji(codePoint)) return false;
        }
        if (CounterKanji.Contains(text[covered[0].S])) return false;
        return before == null || !IsNumerals(text, before.S, before.E);
    }

    /// <returns>The code point when the range holds exactly one, else null</returns>
    private static int? SingleCodePoint(string text, int s, int e)
    {
        if (e - s == 1) return text[s];
        if (e - s == 2 && char.IsSurrogatePair(text[s], text[s + 1])) return char.ConvertToUtf32(text[s], text[s + 1]);
        return null;
    }

    /// <summary>
    /// The index of the token that starts at <paramref name="s"/>, or -1
    /// </summary>
    private static int IndexStartingAt(IReadOnlyList<TokenDto> tokens, int s)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].S == s) return i;
            if (tokens[i].S > s) return -1;
        }
        return -1;
    }

    private static bool HasKanji(string text, int s, int e)
    {
        for (var i = s; i < e; i++)
        {
            var codePoint = i + 1 < e && char.IsSurrogatePair(text[i], text[i + 1]) ? char.ConvertToUtf32(text[i], text[++i]) : text[i];
            if (MarkedReading.IsKanji(codePoint)) return true;
        }
        return false;
    }

    /// <summary>
    /// Whether every character is an ASCII or full-width digit or a kanji numeral
    /// </summary>
    public static bool IsNumerals(string text, int s, int e)
    {
        if (e <= s) return false;
        for (var i = s; i < e; i++)
        {
            var c = text[i];
            if (c is not ((>= '0' and <= '9') or (>= '０' and <= '９')) && !KanjiNumerals.Contains(c)) return false;
        }
        return true;
    }

    private static string Normalise(string value)
    {
        var hiragana = MarkedReading.ToHiragana(value);
        var result = new StringBuilder(hiragana.Length);
        foreach (var c in hiragana) result.Append(Large(c));
        return result.ToString();
    }

    private static string Loose(string normalised)
    {
        var result = new StringBuilder(normalised.Length);
        foreach (var c in normalised)
        {
            if (c != 'つ') result.Append(c);
        }
        return result.ToString();
    }

    private static char Large(char c) => c switch
    {
        'ぁ' or 'ぃ' or 'ぅ' or 'ぇ' or 'ぉ' or 'っ' or 'ゃ' or 'ゅ' or 'ょ' or 'ゎ' => (char) (c + 1),
        'ゕ' => 'か',
        'ゖ' => 'け',
        _ => c,
    };

    /// <summary>
    /// The kana without its voicing marks, so が gives か and ぱ gives は
    /// </summary>
    private static char Unvoiced(char c) => c switch
    {
        'ゔ' => 'う',
        // が to ぢ alternate unvoiced and voiced from か, and つ to ど start again after っ and つ
        >= 'か' and <= 'ぢ' => (char) (c - (c - 'か') % 2),
        >= 'つ' and <= 'ど' => (char) (c - (c - 'つ') % 2),
        // は, ば and ぱ come in threes
        >= 'は' and <= 'ぽ' => (char) (c - (c - 'は') % 3),
        _ => c,
    };
}
