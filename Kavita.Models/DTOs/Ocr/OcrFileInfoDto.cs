using System;
using Kavita.Models.Entities.Enums;

namespace Kavita.Models.DTOs.Ocr;

/// <summary>
/// What the OCR worker needs to know about a file to process its next page and report progress
/// </summary>
public sealed record OcrFileInfoDto
{
    public int MangaFileId { get; init; }
    public string FilePath { get; init; } = string.Empty;
    public int ChapterId { get; init; }
    public int SeriesId { get; init; }
    public int LibraryId { get; init; }
    public string SeriesName { get; init; } = string.Empty;
    public OcrStatus OcrStatus { get; init; }
    public DateTime? OcrSourceWriteTimeUtc { get; init; }
}
