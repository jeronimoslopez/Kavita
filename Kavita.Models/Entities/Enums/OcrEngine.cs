namespace Kavita.Models.Entities.Enums;

/// <summary>
/// Which of the sidecar's engines reads a library's pages
/// </summary>
public enum OcrEngine
{
    /// <summary>
    /// <see cref="Manga"/> for Manga and Comic libraries, <see cref="Books"/> for the rest
    /// </summary>
    Auto = 0,
    /// <summary>
    /// NDLOCR-Lite, for books and magazines
    /// </summary>
    Books = 1,
    /// <summary>
    /// PP-OCRv6_manga, for speech bubbles and hand lettering
    /// </summary>
    Manga = 2,
}
