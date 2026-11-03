namespace Kavita.Models.DTOs.Ocr;

public sealed record OcrSeriesRequestDto
{
    public int SeriesId { get; init; }
    /// <summary>
    /// OCR every page, including pages that already have text, and replace what's there. For PDFs whose own text layer is bad
    /// </summary>
    public bool ReplaceExistingText { get; init; }
}
