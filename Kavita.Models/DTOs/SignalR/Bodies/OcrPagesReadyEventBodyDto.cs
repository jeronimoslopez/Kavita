using System.Collections.Generic;

namespace Kavita.Models.DTOs.SignalR.Bodies;

/// <param name="Pages">1-based page numbers that now have OCR text</param>
public sealed record OcrPagesReadyEventBodyDto(int SeriesId, int ChapterId, IList<int> Pages);
