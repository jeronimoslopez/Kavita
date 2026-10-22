using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using NetVips;

namespace Kavita.Services.Yomitan;

/// <summary>
/// Ports the parts of Yomitan's <c>media-util.js</c> and media loader the importer needs
/// </summary>
public static partial class YomitanMediaUtil
{
    /// <summary>
    /// Browsers fall back to this size for an SVG with no usable width, height or viewBox
    /// </summary>
    private const int DefaultSvgWidth = 300;
    private const int DefaultSvgHeight = 150;

    /// <summary>
    /// Mirrors <c>getImageMediaTypeFromFileName</c>
    /// </summary>
    public static string? GetImageMediaTypeFromFileName(string path)
    {
        var match = FileExtensionRegex().Match(path);
        var extension = match.Success ? match.Value.ToLowerInvariant() : string.Empty;
        return extension switch
        {
            ".apng" => "image/apng",
            ".avif" => "image/avif",
            ".bmp" => "image/bmp",
            ".gif" => "image/gif",
            ".ico" or ".cur" => "image/x-icon",
            ".jpg" or ".jpeg" or ".jfif" or ".pjpeg" or ".pjp" => "image/jpeg",
            ".png" => "image/png",
            ".svg" => "image/svg+xml",
            ".tif" or ".tiff" => "image/tiff",
            ".webp" => "image/webp",
            _ => null,
        };
    }

    /// <summary>
    /// Stands in for the browser's naturalWidth/naturalHeight that Yomitan's media loader reads after decoding the image
    /// </summary>
    /// <exception cref="InvalidDataException">When the image can't be read</exception>
    public static (int Width, int Height) GetImageDimensions(byte[] content, string mediaType)
    {
        if (mediaType == "image/svg+xml")
        {
            return GetSvgDimensions(content);
        }

        try
        {
            // Only the header is read, NetVips decodes pixels lazily
            using var image = Image.NewFromBuffer(content, access: Enums.Access.Sequential);
            return (image.Width, image.Height);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Could not load image ({mediaType})", ex);
        }
    }

    /// <summary>
    /// Reads width and height from the root svg element, falling back to the viewBox and then to the browser default
    /// </summary>
    public static (int Width, int Height) GetSvgDimensions(byte[] content)
    {
        string? widthAttr = null, heightAttr = null, viewBoxAttr = null;
        try
        {
            using var stream = new MemoryStream(content);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                IgnoreComments = true,
            });
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (reader.LocalName != "svg")
                {
                    throw new InvalidDataException("Root element is not svg");
                }
                widthAttr = reader.GetAttribute("width");
                heightAttr = reader.GetAttribute("height");
                viewBoxAttr = reader.GetAttribute("viewBox");
                break;
            }
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException("Could not parse svg", ex);
        }

        var width = ParseSvgLength(widthAttr);
        var height = ParseSvgLength(heightAttr);
        var viewBox = ParseViewBox(viewBoxAttr);

        if (width is null && height is null)
        {
            if (viewBox is { Width: > 0, Height: > 0 })
            {
                return (RoundDimension(viewBox.Value.Width), RoundDimension(viewBox.Value.Height));
            }
            return (DefaultSvgWidth, DefaultSvgHeight);
        }

        // One explicit side, the other follows the viewBox aspect ratio when there is one
        if (width is null)
        {
            width = viewBox is { Width: > 0, Height: > 0 } ? height!.Value * viewBox.Value.Width / viewBox.Value.Height : DefaultSvgWidth;
        }
        else if (height is null)
        {
            height = viewBox is { Width: > 0, Height: > 0 } ? width.Value * viewBox.Value.Height / viewBox.Value.Width : DefaultSvgHeight;
        }

        return (RoundDimension(width.Value), RoundDimension(height!.Value));
    }

    private static int RoundDimension(double value) => (int) Math.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Absolute CSS lengths in px. Percentages and unknown units return null, as they give the image no intrinsic size.
    /// </summary>
    private static double? ParseSvgLength(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = SvgLengthRegex().Match(value.Trim());
        if (!match.Success) return null;
        if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return null;

        double? px = match.Groups[2].Value.ToLowerInvariant() switch
        {
            "" or "px" => number,
            "pt" => number * 96 / 72,
            "pc" => number * 16,
            "in" => number * 96,
            "cm" => number * 96 / 2.54,
            "mm" => number * 96 / 25.4,
            "q" => number * 96 / 101.6,
            "em" or "rem" => number * 16,
            _ => null,
        };
        return px is > 0 ? px : null;
    }

    private static (double Width, double Height)? ParseViewBox(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split([' ', ',', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4) return null;
        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var width)) return null;
        if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var height)) return null;
        return (width, height);
    }

    [GeneratedRegex(@"\.[^./\\]*$")]
    private static partial Regex FileExtensionRegex();

    [GeneratedRegex(@"^([+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)\s*([a-zA-Z%]*)$")]
    private static partial Regex SvgLengthRegex();
}
