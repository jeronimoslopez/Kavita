using System.Collections.Generic;
using Kavita.Models.DTOs.Dictionary;

namespace Kavita.Services.Dictionary;

/// <summary>
/// Decides which reading, if any, the book's or the series' furigana elsewhere gives a word
/// </summary>
public static class ReadingHints
{
    /// <summary>
    /// The share of an ordinary word's uses its one marked reading must cover
    /// </summary>
    public const double MinMarkedShare = 0.2;
    /// <summary>
    /// How many distinct readings an ordinary word may have marked and still get a hint. Two marked readings
    /// disagree, so neither carries to the word's other uses
    /// </summary>
    public const int MaxDistinctReadings = 1;

    /// <summary>
    /// The book's hint when the file alone qualifies, else the series' when all its EPUB files together do
    /// </summary>
    /// <param name="groups">The word's groups, from <see cref="ReadingGroups"/></param>
    /// <param name="book">The word's totals over the chapter's file, or null when it has none there</param>
    /// <param name="series">The word's totals over every EPUB file of the series, or null</param>
    public static ReadingHintDto? Of(bool isProper, IReadOnlyList<ReadingGroupDto> groups, WordMarkTotals? book,
        WordMarkTotals? series)
    {
        if (Qualifying(isProper, groups, book) is { } fromBook)
            return new ReadingHintDto(fromBook.Reading, fromBook.Printed, ReadingSource.Book);
        if (Qualifying(isProper, groups, series) is { } fromSeries)
            return new ReadingHintDto(fromSeries.Reading, fromSeries.Printed, ReadingSource.Series);
        return null;
    }

    /// <summary>
    /// A proper noun takes its most counted marked reading, whatever its groups. An ordinary word qualifies when only
    /// one reading is marked, that reading is in one of its groups, and it covers at least
    /// <see cref="MinMarkedShare"/> of the word's uses
    /// </summary>
    /// <returns>The marked reading the source gives, or null when it gives none</returns>
    public static ReadingMarkTotal? Qualifying(bool isProper, IReadOnlyList<ReadingGroupDto> groups, WordMarkTotals? totals)
    {
        if (totals == null || totals.Readings.Count == 0) return null;

        // Readings come most counted first
        if (isProper) return totals.Readings[0];

        if (totals.Readings.Count > MaxDistinctReadings || totals.Uses <= 0) return null;
        var only = totals.Readings[0];
        if (!ReadingGroups.AnyHas(groups, only.Reading)) return null;
        return (double) only.Count / totals.Uses >= MinMarkedShare ? only : null;
    }
}
