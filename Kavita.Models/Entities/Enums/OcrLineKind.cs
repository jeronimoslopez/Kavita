namespace Kavita.Models.Entities.Enums;

/// <summary>
/// The layout role the OCR engine gave a line. The reader only draws <see cref="Body"/>, <see cref="Title"/> and <see cref="Caption"/>
/// </summary>
public enum OcrLineKind
{
    Body = 0,
    Title = 1,
    Caption = 2,
    /// <summary>
    /// Furigana, kept so it can be shown on demand
    /// </summary>
    Ruby = 3,
    /// <summary>
    /// Running head
    /// </summary>
    Header = 4,
    /// <summary>
    /// Page number
    /// </summary>
    Folio = 5,
}
