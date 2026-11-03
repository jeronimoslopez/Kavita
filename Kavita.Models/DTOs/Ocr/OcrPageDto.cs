using System.Collections.Generic;
using Kavita.Models.Entities;

namespace Kavita.Models.DTOs.Ocr;

public sealed record OcrPageDto
{
    /// <summary>
    /// 1-based, matching pdf.js
    /// </summary>
    public int PageNumber { get; init; }
    /// <summary>
    /// Changes whenever the page is OCR'd again, so the reader can cache by it
    /// </summary>
    public long Version { get; init; }
    public IList<OcrLine> Lines { get; init; } = [];
}
