using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kavita.Models.DTOs.Dictionary;
#nullable enable

/// <summary>
/// One furigana span of a block, the reading a book prints over part of the block's text. On the wire it's the array
/// <c>[s, e, reading]</c>, or <c>[s, e, reading, group]</c> when it has a group, in reading.db and in the page tokens
/// the reader gets
/// </summary>
/// <param name="S">UTF-16 offset of the base text's first character in its block's text</param>
/// <param name="E">UTF-16 offset just past the base text's last character</param>
/// <param name="Reading">The <c>rt</c> text as the book prints it, in hiragana, katakana or anything else the book
/// puts there</param>
/// <param name="Group">The same number for every span of one <c>ruby</c> element of the block, such as the two spans
/// of <c>&lt;ruby&gt;&lt;rb&gt;羅&lt;/rb&gt;&lt;rt&gt;ラ&lt;/rt&gt;&lt;rb&gt;半&lt;/rb&gt;&lt;rt&gt;ハン&lt;/rt&gt;&lt;/ruby&gt;</c>.
/// Null, and left out of the array, when the element gives one span</param>
[JsonConverter(typeof(RubyJsonConverter))]
public sealed record RubyDto(int S, int E, string Reading, int? Group = null);

public sealed class RubyJsonConverter : JsonConverter<RubyDto>
{
    public override RubyDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        TokenArray.Start(ref reader, "ruby span");
        var s = TokenArray.ReadInt(ref reader);
        var e = TokenArray.ReadInt(ref reader);
        var reading = TokenArray.ReadNullableString(ref reader) ?? throw new JsonException("A ruby span's reading can't be null");

        if (!reader.Read()) throw new JsonException("A ruby span must have 3 or 4 items");
        if (reader.TokenType == JsonTokenType.EndArray) return new RubyDto(s, e, reading);

        int? group = reader.TokenType == JsonTokenType.Null ? null : reader.GetInt32();
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
            throw new JsonException("A ruby span must have 3 or 4 items");
        return new RubyDto(s, e, reading, group);
    }

    public override void Write(Utf8JsonWriter writer, RubyDto value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.S);
        writer.WriteNumberValue(value.E);
        writer.WriteStringValue(value.Reading);
        if (value.Group is { } group) writer.WriteNumberValue(group);
        writer.WriteEndArray();
    }
}
