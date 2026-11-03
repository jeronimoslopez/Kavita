using System.Collections.Generic;
using Kavita.Models.Entities.Enums;

namespace Kavita.Models.DTOs.Ocr;

public sealed record OcrChapterStatusDto
{
    public OcrStatus Status { get; init; }
    /// <summary>
    /// 1-based numbers of the pages that have OCR text, matching pdf.js
    /// </summary>
    public IList<int> Pages { get; init; } = [];
    public int PendingPages { get; init; }
    public int FailedPages { get; init; }
}
