using System;
using System.Collections.Generic;
using System.Linq;
using Kavita.Models.DTOs.Dictionary;

namespace Kavita.Services.Dictionary;

/// <summary>
/// One reading_mark row of a page
/// </summary>
/// <param name="Reading">The dictionary-form reading in hiragana</param>
/// <param name="Printed">The page's most frequent printed form of that reading</param>
/// <param name="Count">How many top-level tokens of the word the page's furigana reads this way</param>
internal sealed record PageMark(long WordId, string Reading, string Printed, int Count);

/// <summary>
/// The reading_mark and word_use rows one page's blocks give
/// </summary>
internal static class PageMarks
{
    /// <param name="lemmas">Lemma by word id. A token whose word has no entry gets no mark, and still counts as a use</param>
    /// <returns>The marks ordered by word and reading, and the uses by word. Only top-level tokens with a word count,
    /// never their parts or the blocks' phrases</returns>
    public static (IReadOnlyList<PageMark> Marks, IReadOnlyDictionary<long, int> Uses) Count(
        IEnumerable<StoredBlock> blocks, IReadOnlyDictionary<long, string> lemmas)
    {
        var uses = new Dictionary<long, int>();
        var printed = new Dictionary<(long WordId, string Reading), Dictionary<string, int>>();
        foreach (var block in blocks)
        {
            foreach (var token in block.Tokens)
            {
                if (token.W is not { } wordId) continue;
                uses[wordId] = uses.GetValueOrDefault(wordId) + 1;
                if (block.Ruby.Count == 0) continue;

                var marked = MarkedReading.Of(block.Text, token, block.Ruby, lemmas.GetValueOrDefault(wordId));
                if (marked is not { DictionaryReading: { } reading, DictionaryPrinted: { } form }) continue;

                if (!printed.TryGetValue((wordId, reading), out var forms))
                {
                    forms = new Dictionary<string, int>(StringComparer.Ordinal);
                    printed[(wordId, reading)] = forms;
                }
                forms[form] = forms.GetValueOrDefault(form) + 1;
            }
        }

        var marks = printed
            .Select(p => new PageMark(p.Key.WordId, p.Key.Reading,
                p.Value.OrderByDescending(f => f.Value).ThenBy(f => f.Key, StringComparer.Ordinal).First().Key,
                p.Value.Values.Sum()))
            .OrderBy(m => m.WordId).ThenBy(m => m.Reading, StringComparer.Ordinal)
            .ToList();
        return (marks, uses);
    }
}
