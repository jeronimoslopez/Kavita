using System.Collections.Generic;
using System.Linq;
using Kavita.Models.DTOs.Dictionary;

namespace Kavita.Services.Dictionary;

/// <summary>
/// Splits a word's senses into the groups the popup shows one tab each for
/// </summary>
public static class ReadingGroups
{
    /// <summary>
    /// For each entry in order, its readings fall into classes, two readings sharing a class when exactly the same
    /// senses of the entry apply to both. A sense applies to a reading when it has no <see cref="SenseDto.R"/> or its
    /// <see cref="SenseDto.R"/> holds the reading. Classes keep the order of their first reading, and each class that
    /// some sense applies to is a group. Groups with equal readings lists then merge into the first of them, since
    /// their tabs would offer no choice of reading. A word whose lemma has no kanji has one group, read as the lemma
    /// in hiragana, because its reading is what the book prints
    /// </summary>
    /// <param name="entries">The word's entries, in the order their senses appear</param>
    /// <param name="senses">The word's senses. A sense belongs to the entry whose seq its key starts with</param>
    /// <param name="lemma">The word's lemma, or null to group by the entries alone</param>
    /// <returns>Empty when the word has no entries, as a word from a sidecar before version 3 has</returns>
    public static IReadOnlyList<ReadingGroupDto> Of(IReadOnlyList<WordEntryDto> entries, IReadOnlyList<SenseDto> senses,
        string? lemma = null)
    {
        if (entries.Count == 0) return [];
        if (lemma != null && !HasKanji(lemma))
        {
            return [new ReadingGroupDto(entries[0].Seq, [MarkedReading.ToHiragana(lemma)], entries.Any(e => e.Common),
                senses.ToList())];
        }

        var groups = new List<ReadingGroupDto>();
        foreach (var entry in entries)
        {
            var entrySenses = senses.Where(s => s.Seq == entry.Seq).ToList();
            var classes = new List<(List<int> Applies, List<string> Readings)>();
            foreach (var reading in entry.Readings)
            {
                var applies = entrySenses
                    .Select((sense, index) => (sense, index))
                    .Where(s => s.sense.R == null || s.sense.R.Contains(reading))
                    .Select(s => s.index)
                    .ToList();
                var same = classes.FindIndex(c => c.Applies.SequenceEqual(applies));
                if (same >= 0) classes[same].Readings.Add(reading);
                else classes.Add((applies, [reading]));
            }

            groups.AddRange(classes
                .Where(c => c.Applies.Count > 0)
                .Select(c => new ReadingGroupDto(entry.Seq, c.Readings, entry.Common,
                    c.Applies.Select(i => entrySenses[i]).ToList())));
        }
        return MergeEqualReadings(groups);
    }

    /// <returns>Empty for a word stored without entries</returns>
    public static IReadOnlyList<ReadingGroupDto> Of(StoredWord word) => Of(word.Entries ?? [], word.Senses, word.Lemma);

    /// <returns>True when some group has <paramref name="reading"/> among its readings</returns>
    public static bool AnyHas(IEnumerable<ReadingGroupDto> groups, string? reading) =>
        reading != null && groups.Any(g => g.Readings.Contains(reading));

    /// <summary>
    /// Folds each group into the first group whose readings are the same in the same order. The merged group sits
    /// where that first group sat, keeps its seq, holds the senses of all of them in order, and is common when any is
    /// </summary>
    private static List<ReadingGroupDto> MergeEqualReadings(List<ReadingGroupDto> groups)
    {
        var merged = new List<ReadingGroupDto>();
        foreach (var group in groups)
        {
            var same = merged.FindIndex(g => g.Readings.SequenceEqual(group.Readings));
            if (same < 0)
            {
                merged.Add(group);
                continue;
            }
            var first = merged[same];
            merged[same] = first with
            {
                Common = first.Common || group.Common,
                Senses = first.Senses.Concat(group.Senses).ToList(),
            };
        }
        return merged;
    }

    private static bool HasKanji(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var codePoint = i + 1 < text.Length && char.IsSurrogatePair(text[i], text[i + 1])
                ? char.ConvertToUtf32(text[i], text[++i])
                : text[i];
            if (MarkedReading.IsKanji(codePoint)) return true;
        }
        return false;
    }
}
