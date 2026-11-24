using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Kavita.Services.Dictionary;

/// <summary>
/// How reading.db writes the JSON in word.readings and word.senses, and the JSON that unit.blocks compresses
/// </summary>
/// <remarks>
/// The default encoder writes every Japanese character as a six-byte \uXXXX escape. The relaxed one writes kana and
/// kanji as UTF-8, three bytes each, and still escapes characters outside the BMP and a few spaces such as U+3000.
/// It doesn't escape HTML-sensitive characters either, which is safe because only the store reads this JSON back and
/// it's never written into a page. Any JSON parser reads both forms, so rows written before stay readable
/// </remarks>
internal static class ReadingJson
{
    public static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <returns>The same JSON value written with <see cref="Options"/>'s encoder</returns>
    public static string Reencode(string json)
    {
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream(json.Length);
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = Options.Encoder }))
        {
            document.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int) stream.Length);
    }
}
