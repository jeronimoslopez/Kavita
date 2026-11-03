using Kavita.Models.Entities.Enums;

namespace Kavita.Models.Entities;

/// <summary>
/// One line of OCR text on a page. Stored as JSON on <see cref="OcrPage.Lines"/>
/// </summary>
/// <remarks>
/// The box is a fraction of the rendered page image, which PDFium draws from the page's CropBox. pdf.js builds its page view
/// from the same box, so the reader can place a line without knowing the PDF's units
/// </remarks>
public sealed record OcrLine
{
    /// <summary>
    /// Reading order on the page, starting at 0
    /// </summary>
    public int Order { get; init; }
    public string Text { get; init; } = string.Empty;
    public float X { get; init; }
    public float Y { get; init; }
    public float Width { get; init; }
    public float Height { get; init; }
    public bool Vertical { get; init; }
    public OcrLineKind Kind { get; init; }
    /// <summary>
    /// How sure the detector was that this box holds a line, 0 to 1
    /// </summary>
    public float DetectionConfidence { get; init; }
    /// <summary>
    /// How sure the recognizer was of its least certain character, 0 to 1
    /// </summary>
    public float RecognitionConfidence { get; init; }
}
