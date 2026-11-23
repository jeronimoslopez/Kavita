using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kavita.Models.DTOs.Dictionary;
#nullable enable

/// <summary>
/// One token of a block's text. On the wire it's the array <c>[s, e, w, r, sub]</c>, in the text sidecar's response,
/// in reading.db and in the page tokens the reader gets
/// </summary>
/// <param name="S">UTF-16 offset of the token's first character in its block's text</param>
/// <param name="E">UTF-16 offset just past the token's last character</param>
/// <param name="W">In a sidecar response, an index into its <c>words</c>. Everywhere after that, a reading.db word id.
/// Null when the token isn't a word, such as punctuation</param>
/// <param name="R">Hiragana reading of this occurrence, or null</param>
/// <param name="Sub">The short-unit parts of a compound, or null when the token has none</param>
[JsonConverter(typeof(TokenJsonConverter))]
public sealed record TokenDto(int S, int E, long? W, string? R, IList<SubTokenDto>? Sub);

/// <summary>
/// One short-unit part of a compound token. On the wire it's the array <c>[s, e, w, r]</c>, with the same meanings as
/// <see cref="TokenDto"/> and offsets into the same block text
/// </summary>
[JsonConverter(typeof(SubTokenJsonConverter))]
public sealed record SubTokenDto(int S, int E, long? W, string? R);

public sealed class TokenJsonConverter : JsonConverter<TokenDto>
{
    public override TokenDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        TokenArray.Start(ref reader, "token");
        var s = TokenArray.ReadInt(ref reader);
        var e = TokenArray.ReadInt(ref reader);
        var w = TokenArray.ReadNullableLong(ref reader);
        var r = TokenArray.ReadNullableString(ref reader);

        TokenArray.Next(ref reader);
        List<SubTokenDto>? sub = null;
        if (reader.TokenType != JsonTokenType.Null)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("A token's sub must be an array or null");
            sub = [];
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                sub.Add(SubTokenJsonConverter.ReadSub(ref reader));
            }
        }

        TokenArray.End(ref reader, "token", 5);
        return new TokenDto(s, e, w, r, sub);
    }

    public override void Write(Utf8JsonWriter writer, TokenDto value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        TokenArray.WriteSpan(writer, value.S, value.E, value.W, value.R);
        if (value.Sub == null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartArray();
            foreach (var part in value.Sub) SubTokenJsonConverter.WriteSub(writer, part);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }
}

public sealed class SubTokenJsonConverter : JsonConverter<SubTokenDto>
{
    public override SubTokenDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ReadSub(ref reader);

    public override void Write(Utf8JsonWriter writer, SubTokenDto value, JsonSerializerOptions options) =>
        WriteSub(writer, value);

    internal static SubTokenDto ReadSub(ref Utf8JsonReader reader)
    {
        TokenArray.Start(ref reader, "sub token");
        var s = TokenArray.ReadInt(ref reader);
        var e = TokenArray.ReadInt(ref reader);
        var w = TokenArray.ReadNullableLong(ref reader);
        var r = TokenArray.ReadNullableString(ref reader);
        TokenArray.End(ref reader, "sub token", 4);
        return new SubTokenDto(s, e, w, r);
    }

    internal static void WriteSub(Utf8JsonWriter writer, SubTokenDto value)
    {
        writer.WriteStartArray();
        TokenArray.WriteSpan(writer, value.S, value.E, value.W, value.R);
        writer.WriteEndArray();
    }
}

internal static class TokenArray
{
    public static void Start(ref Utf8JsonReader reader, string what)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException($"A {what} must be a JSON array");
    }

    public static void Next(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType == JsonTokenType.EndArray) throw new JsonException("A token array is too short");
    }

    public static void End(ref Utf8JsonReader reader, string what, int length)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
            throw new JsonException($"A {what} must have exactly {length} items");
    }

    public static int ReadInt(ref Utf8JsonReader reader)
    {
        Next(ref reader);
        return reader.GetInt32();
    }

    public static long? ReadNullableLong(ref Utf8JsonReader reader)
    {
        Next(ref reader);
        return reader.TokenType == JsonTokenType.Null ? null : reader.GetInt64();
    }

    public static string? ReadNullableString(ref Utf8JsonReader reader)
    {
        Next(ref reader);
        return reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
    }

    public static void WriteSpan(Utf8JsonWriter writer, int s, int e, long? w, string? r)
    {
        writer.WriteNumberValue(s);
        writer.WriteNumberValue(e);
        if (w == null) writer.WriteNullValue(); else writer.WriteNumberValue(w.Value);
        if (r == null) writer.WriteNullValue(); else writer.WriteStringValue(r);
    }
}
