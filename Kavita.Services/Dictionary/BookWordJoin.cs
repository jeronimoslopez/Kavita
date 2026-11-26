using System;
using System.Collections.Generic;
using System.Linq;
using Kavita.Models.DTOs.Dictionary;

namespace Kavita.Services.Dictionary;

/// <summary>
/// One book word as a series reads it
/// </summary>
/// <param name="Text">The word as the book writes it</param>
/// <param name="WordId">The word row the joined token points at</param>
/// <param name="Hint">The reading the opened file gives it, or the series when the file has none</param>
public sealed record SeriesBookWord(string Text, long WordId, ReadingHintDto Hint);

/// <summary>
/// The book words of one series as the opened file sees them, longest first so a longer word wins over one inside it
/// </summary>
public sealed class BookWordSet
{
    public static readonly BookWordSet Empty = new([]);

    private BookWordSet(IReadOnlyList<SeriesBookWord> words)
    {
        Words = words;
        Hints = words.ToDictionary(w => w.WordId, w => w.Hint);
    }

    /// <summary>
    /// Ordered by length, longest first, then by text in ordinal order
    /// </summary>
    public IReadOnlyList<SeriesBookWord> Words { get; }

    /// <summary>
    /// Each book word's hint by its word id
    /// </summary>
    public IReadOnlyDictionary<long, ReadingHintDto> Hints { get; }

    /// <summary>
    /// One reading per text. A text the opened file defines takes the file's rows and the source
    /// <see cref="ReadingSource.Book"/>, and any other takes all the series' rows and <see cref="ReadingSource.Series"/>.
    /// The reading is the one those rows count most, ties to the first in ordinal order, printed the way they print it
    /// most, with the same tie rule
    /// </summary>
    /// <param name="totals">The book words of every EPUB file of the series, the opened file's included</param>
    /// <param name="mangaFileId">The opened file</param>
    public static BookWordSet From(IReadOnlyList<BookWordTotal> totals, int mangaFileId)
    {
        if (totals.Count == 0) return Empty;

        var words = new List<SeriesBookWord>();
        foreach (var text in totals.GroupBy(t => t.Text, StringComparer.Ordinal))
        {
            var own = text.Where(t => t.MangaFileId == mangaFileId).ToList();
            var rows = own.Count > 0 ? own : text.ToList();
            var source = own.Count > 0 ? ReadingSource.Book : ReadingSource.Series;

            var best = rows
                .GroupBy(t => t.Reading, StringComparer.Ordinal)
                .Select(r => (Reading: r.Key, Count: r.Sum(t => t.Count), Printed: r
                    .GroupBy(t => t.Printed, StringComparer.Ordinal)
                    .OrderByDescending(p => p.Sum(t => t.Count)).ThenBy(p => p.Key, StringComparer.Ordinal)
                    .First().Key))
                .OrderByDescending(r => r.Count).ThenBy(r => r.Reading, StringComparer.Ordinal)
                .First();
            words.Add(new SeriesBookWord(text.Key, rows[0].WordId, new ReadingHintDto(best.Reading, best.Printed, source)));
        }

        return new BookWordSet(words
            .OrderByDescending(w => w.Text.Length).ThenBy(w => w.Text, StringComparer.Ordinal)
            .ToList());
    }
}

/// <summary>
/// Joins the tokens a book word covers into one token, at read time, so every stored page picks up the book words
/// the series defines without being tokenized again
/// </summary>
public static class BookWordJoin
{
    /// <returns>The unit with every block joined, or the same unit when the set is empty</returns>
    public static ReadingUnit Apply(ReadingUnit unit, BookWordSet set)
    {
        if (set.Words.Count == 0) return unit;

        var changed = false;
        var blocks = new List<StoredBlock>(unit.Blocks.Count);
        foreach (var block in unit.Blocks)
        {
            var joined = Apply(block, set);
            changed |= !ReferenceEquals(joined, block);
            blocks.Add(joined);
        }
        return changed ? unit with { Blocks = blocks } : unit;
    }

    /// <summary>
    /// Finds each book word in the block's text, longest first, and joins the tokens of each occurrence that starts at
    /// one token's start, ends at a later token's end and shares no token with an earlier match. The joined token
    /// points at the book word's word, has no reading of its own, and keeps the covered tokens as its parts, each
    /// without the parts it had. A phrase that no longer starts and ends on two different top-level tokens is dropped,
    /// and so is a blind offset inside a joined token
    /// </summary>
    /// <returns>The same block when nothing joins</returns>
    public static StoredBlock Apply(StoredBlock block, BookWordSet set)
    {
        var tokens = block.Tokens;
        if (set.Words.Count == 0 || tokens.Count < 2) return block;

        var text = block.Text;
        int[]? startAt = null, endAt = null;
        bool[]? taken = null;
        List<(int First, int Last, SeriesBookWord Word)>? matches = null;
        foreach (var word in set.Words)
        {
            for (var at = text.IndexOf(word.Text, StringComparison.Ordinal); at >= 0;
                 at = text.IndexOf(word.Text, at + 1, StringComparison.Ordinal))
            {
                if (startAt == null) (startAt, endAt, taken) = Boundaries(text, tokens);
                var first = startAt[at];
                var last = endAt![at + word.Text.Length];
                if (first < 0 || last <= first) continue;

                var free = true;
                for (var i = first; i <= last && free; i++) free = !taken![i];
                if (!free) continue;

                for (var i = first; i <= last; i++) taken![i] = true;
                (matches ??= []).Add((first, last, word));
            }
        }
        if (matches == null) return block;

        matches.Sort((a, b) => a.First.CompareTo(b.First));
        var joined = new List<TokenDto>(tokens.Count);
        var next = 0;
        foreach (var (first, last, word) in matches)
        {
            for (; next < first; next++) joined.Add(tokens[next]);
            var parts = new List<SubTokenDto>(last - first + 1);
            for (var i = first; i <= last; i++) parts.Add(new SubTokenDto(tokens[i].S, tokens[i].E, tokens[i].W, tokens[i].R));
            joined.Add(new TokenDto(tokens[first].S, tokens[last].E, word.WordId, null, parts));
            next = last + 1;
        }
        for (; next < tokens.Count; next++) joined.Add(tokens[next]);

        var spans = matches.Select(m => (S: tokens[m.First].S, E: tokens[m.Last].E)).ToList();
        var starts = new Dictionary<int, int>();
        var ends = new Dictionary<int, int>();
        for (var i = 0; i < joined.Count; i++)
        {
            starts.TryAdd(joined[i].S, i);
            ends.TryAdd(joined[i].E, i);
        }

        return block with
        {
            Tokens = joined,
            Phrases = block.Phrases
                .Where(p => starts.TryGetValue(p.S, out var s) && ends.TryGetValue(p.E, out var e) && e > s)
                .ToList(),
            Blind = block.Blind.Where(b => !spans.Any(span => span.S <= b && b < span.E)).ToList(),
        };
    }

    /// <returns>For each offset of the text, the index of the token starting there and of the token ending there, or
    /// -1, and an empty mark per token</returns>
    private static (int[] StartAt, int[] EndAt, bool[] Taken) Boundaries(string text, IReadOnlyList<TokenDto> tokens)
    {
        var startAt = new int[text.Length + 1];
        var endAt = new int[text.Length + 1];
        Array.Fill(startAt, -1);
        Array.Fill(endAt, -1);
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.S < 0 || token.E > text.Length || token.E <= token.S) continue;
            if (startAt[token.S] < 0) startAt[token.S] = i;
            endAt[token.E] = i;
        }
        return (startAt, endAt, new bool[tokens.Count]);
    }
}
