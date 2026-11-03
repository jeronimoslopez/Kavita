namespace Kavita.Models.Entities.Enums;

/// <summary>
/// Where a file stands in OCR. Only PDFs and single-archive chapters in libraries with OCR turned on move past
/// <see cref="NotChecked"/>
/// </summary>
public enum OcrStatus
{
    /// <summary>
    /// Pages haven't been checked for text yet, or the file changed since they were
    /// </summary>
    NotChecked = 0,
    /// <summary>
    /// Every page of a PDF already has text, so there's nothing to OCR
    /// </summary>
    HasText = 1,
    /// <summary>
    /// At least one page has no text and is waiting for OCR
    /// </summary>
    Pending = 2,
    Processing = 3,
    Done = 4,
    Failed = 5,
    /// <summary>
    /// An admin removed the OCR text. Rescans leave the file alone until OCR is run again
    /// </summary>
    Removed = 6,
}
