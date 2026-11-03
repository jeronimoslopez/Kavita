using System;
using System.Collections.Generic;
using Kavita.Models.Entities.Enums;
using Kavita.Models.Entities.Interfaces;

namespace Kavita.Models.Entities;
#nullable enable

/// <summary>
/// A PDF page with no text of its own, and the OCR text for it once the sidecar has read it
/// </summary>
public class OcrPage : IEntityDate
{
    public int Id { get; set; }
    /// <summary>
    /// 0-based, matching Docnet. pdf.js counts from 1
    /// </summary>
    public int PageNumber { get; set; }
    public OcrPageState State { get; set; }
    /// <summary>
    /// How many times the sidecar has rejected this page
    /// </summary>
    public int Attempts { get; set; }
    public IList<OcrLine> Lines { get; set; } = [];
    public string? Engine { get; set; }
    public string? EngineVersion { get; set; }
    /// <summary>
    /// Size of the image sent to the sidecar, in pixels
    /// </summary>
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }

    public DateTime Created { get; set; }
    public DateTime LastModified { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime LastModifiedUtc { get; set; }

    public int MangaFileId { get; set; }
    public MangaFile MangaFile { get; set; } = null!;
}
