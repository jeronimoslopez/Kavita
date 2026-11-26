using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kavita.Models.DTOs.Dictionary;

namespace Kavita.Services.Dictionary;

/// <summary>
/// How unit.blocks is stored. Every read and write of the column goes through <see cref="Encode"/> and
/// <see cref="Decode"/>
/// </summary>
/// <remarks>
/// A row is a BLOB of one format byte and its payload. <see cref="BrotliJson"/> is UTF-8 JSON compressed with
/// Brotli at quality 5. Token arrays are most of a page, and on a 10,000-character page quality 5 came out about 9%
/// smaller than GZip at Optimal, for about a millisecond more per save. Rows written before compression hold JSON
/// text, escaped or not, and still read. SQLite can't see inside the BLOB, so nothing may query the column with
/// json_extract or json_each
/// </remarks>
internal static class ReadingBlocks
{
    public const byte BrotliJson = 0x01;
    private const int BrotliQuality = 5;

    public static byte[] Encode(IReadOnlyList<StoredBlock> blocks)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(
            blocks.Select(b => new BlockJson(b.Index, b.Text, b.Tokens, b.Phrases.Count == 0 ? null : b.Phrases,
                b.Ruby.Count == 0 ? null : b.Ruby)).ToList(),
            ReadingJson.Options);
        using var output = new MemoryStream(json.Length / 3 + 16);
        output.WriteByte(BrotliJson);
        using (var brotli = new BrotliStream(output,
                   new BrotliCompressionOptions { Quality = BrotliQuality }, leaveOpen: true))
        {
            brotli.Write(json);
        }
        return output.ToArray();
    }

    /// <param name="value">The column as Microsoft.Data.Sqlite returns it, a byte array for a BLOB or a string for
    /// JSON text</param>
    /// <param name="unitId">Named in the exception when the value can't be read</param>
    /// <exception cref="InvalidDataException">The value is a BLOB with an unknown format byte, or its payload is
    /// damaged</exception>
    public static List<StoredBlock> Decode(object value, long unitId)
    {
        try
        {
            var json = value switch
            {
                string text => JsonSerializer.Deserialize<List<BlockJson>>(text),
                byte[] { Length: > 0 } blob when blob[0] == BrotliJson => DecompressJson(blob),
                byte[] { Length: > 0 } blob => throw new InvalidDataException($"format byte 0x{blob[0]:X2} is unknown"),
                _ => throw new InvalidDataException($"a {value.GetType().Name} isn't a stored form"),
            };
            // A block written without tokens, phrases or ruby has none, so callers never meet a null list
            return (json ?? [])
                .Select(b => new StoredBlock(b.Index, b.Text, b.Tokens ?? []) { Phrases = b.Phrases ?? [], Ruby = b.Ruby ?? [] })
                .ToList();
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException)
        {
            throw new InvalidDataException($"The blocks of reading.db unit {unitId} can't be read: {ex.Message}", ex);
        }
    }

    /// <returns>True when the value is already in the current form, so the upgrade can skip it</returns>
    public static bool IsCurrent(object value) => value is byte[] { Length: > 0 } blob && blob[0] == BrotliJson;

    private static List<BlockJson>? DecompressJson(byte[] blob)
    {
        using var brotli = new BrotliStream(new MemoryStream(blob, 1, blob.Length - 1), CompressionMode.Decompress);
        return JsonSerializer.Deserialize<List<BlockJson>>(brotli);
    }

    /// <summary>
    /// A block in unit.blocks. Tokens, phrases and ruby spans use their DTOs' array converters, the same form the
    /// sidecar uses. A block with no phrases or no ruby is written without that key, the form every block had before
    /// they existed, so the format byte stays the same and blocks stored earlier read with neither
    /// </summary>
    private sealed record BlockJson(
        [property: JsonPropertyName("i")] int Index,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("tokens")] IReadOnlyList<TokenDto>? Tokens,
        [property: JsonPropertyName("phrases"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<PhraseDto>? Phrases = null,
        [property: JsonPropertyName("ruby"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<RubyDto>? Ruby = null);
}
