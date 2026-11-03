using System;
using System.Globalization;

namespace Kavita.Services.Ocr;

public static class OcrSchedule
{
    /// <summary>
    /// Whether OCR may run at <paramref name="now"/> given the OcrWindowStart and OcrWindowEnd settings
    /// </summary>
    /// <remarks>
    /// Either time empty or unreadable means no window. A window that ends before it starts runs overnight,
    /// and one that starts and ends at the same minute covers the whole day
    /// </remarks>
    public static bool IsWithinWindow(TimeOnly now, string? start, string? end)
    {
        if (!TryParse(start, out var from) || !TryParse(end, out var to)) return true;
        if (from == to) return true;
        return from < to ? now >= from && now < to : now >= from || now < to;
    }

    private static bool TryParse(string? value, out TimeOnly time)
    {
        return TimeOnly.TryParseExact((value ?? string.Empty).Trim(), "HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out time);
    }
}
