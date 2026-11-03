using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Kavita.Models.DTOs.Ocr;
#nullable enable

/// <summary>
/// The OCR sidecar's answer to <c>POST /ocr</c>. The contract is <c>ocr-sidecar/contract/ocr-response.schema.json</c>
/// </summary>
public sealed record OcrSidecarResponseDto
{
    [JsonPropertyName("engine")]
    public string Engine { get; init; } = string.Empty;
    [JsonPropertyName("engineVersion")]
    public string EngineVersion { get; init; } = string.Empty;
    [JsonPropertyName("imageWidth")]
    public int ImageWidth { get; init; }
    [JsonPropertyName("imageHeight")]
    public int ImageHeight { get; init; }
    [JsonPropertyName("lines")]
    public IList<OcrSidecarLineDto> Lines { get; init; } = [];
}

public sealed record OcrSidecarLineDto
{
    [JsonPropertyName("order")]
    public int Order { get; init; }
    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;
    /// <summary>
    /// [x, y, width, height] in pixels of the posted image, origin top left
    /// </summary>
    [JsonPropertyName("box")]
    public int[] Box { get; init; } = [];
    [JsonPropertyName("vertical")]
    public bool Vertical { get; init; }
    /// <summary>
    /// body, caption, title, ruby, header or folio
    /// </summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;
    [JsonPropertyName("detConf")]
    public float DetConf { get; init; }
    [JsonPropertyName("recConf")]
    public float RecConf { get; init; }
}
