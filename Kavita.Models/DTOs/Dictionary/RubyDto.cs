using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kavita.Models.DTOs.Dictionary;
#nullable enable

/// <summary>
/// One furigana span of a block, the reading a book prints over part of the block's text. On the wire it's the array
/// <c>[s, e, reading]</c>, in reading.db and in the page tokens the reader gets
/// </summary>
/// <param name="S">UTF-16 offset of the base text's first character in its block's text</param>
/// <param name="E">UTF-16 offset just past the base text's last character</param>
/// <param name="Reading">The <c>rt</c> text as the book prints it, in hiragana, katakana or anything else the book
/// puts there</param>
[JsonConverter(typeof(RubyJsonConverter))]
public sealed record RubyDto(int S, int E, string Reading);

public sealed class RubyJsonConverter : JsonConverter<RubyDto>
{
    public override RubyDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        TokenArray.Start(ref reader, "ruby span");
        var s = TokenArray.ReadInt(ref reader);
        var e = TokenArray.ReadInt(ref reader);
        var reading = TokenArray.ReadNullableString(ref reader) ?? throw new JsonException("A ruby span's reading can't be null");
        TokenArray.End(ref reader, "ruby span", 3);
        return new RubyDto(s, e, reading);
    }

    public override void Write(Utf8JsonWriter writer, RubyDto value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.S);
        writer.WriteNumberValue(value.E);
        writer.WriteStringValue(value.Reading);
        writer.WriteEndArray();
    }
}
