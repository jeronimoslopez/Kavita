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
    /// some sense applies to is a group
    /// </summary>
    /// <param name="entries">The word's entries, in the order their senses appear</param>
    /// <param name="senses">The word's senses. A sense belongs to the entry whose seq its key starts with</param>
    /// <returns>Empty when the word has no entries, as a word from a sidecar before version 3 has</returns>
    public static IReadOnlyList<ReadingGroupDto> Of(IReadOnlyList<WordEntryDto> entries, IReadOnlyList<SenseDto> senses)
    {
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
        return groups;
    }

    /// <returns>Empty for a word stored without entries</returns>
    public static IReadOnlyList<ReadingGroupDto> Of(StoredWord word) => Of(word.Entries ?? [], word.Senses);

    /// <returns>True when some group has <paramref name="reading"/> among its readings</returns>
    public static bool AnyHas(IEnumerable<ReadingGroupDto> groups, string? reading) =>
        reading != null && groups.Any(g => g.Readings.Contains(reading));
}
