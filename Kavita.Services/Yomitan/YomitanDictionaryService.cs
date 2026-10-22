using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kavita.API.Services;
using Kavita.Models.DTOs.Yomitan;
using Microsoft.Data.Sqlite;

namespace Kavita.Services.Yomitan;

/// <summary>
/// Query side of <see cref="YomitanDictionaryDatabase"/>. Each method runs one statement on one connection. The request
/// list goes in as a JSON array and is expanded with json_each, which drives an index lookup per item, so a lookup that
/// sends a few hundred candidate strings is still a single round trip.
/// </summary>
/// <remarks>Dictionary filters are written as <c>+column</c> so SQLite can't pick the dictionary index over the key index,
/// which would scan every row of a dictionary for each item.
/// Result shapes and ordering follow <c>DictionaryDatabase._findMultiBulk</c>: items in request order, then index
/// (expression before reading), then index key, then row id.</remarks>
public class YomitanDictionaryService(YomitanDictionaryDatabase database, YomitanImportProgress progress) : IYomitanDictionaryService
{
    private const string TermColumns = "t.id, t.expression, t.reading, t.definitionTags, t.rules, t.score, t.glossary, t.dictionary, t.sequence, t.termTags";

    /// <summary>
    /// Upper bound for prefix ranges. Yomitan uses ￿ on UTF-16 keys, SQLite compares UTF-8 bytes, so the highest code
    /// point is needed to cover characters outside the BMP.
    /// </summary>
    private const string PrefixUpperBound = "char(1114111)";

    public async Task<IList<YomitanTermEntryDto>> FindTermsBulkAsync(IList<string> termList, IList<string> dictionaries, string matchType, CancellationToken ct = default)
    {
        if (termList.Count == 0 || dictionaries.Count == 0) return [];

        var (expressionColumn, readingColumn) = matchType == "suffix"
            ? ("expressionReverse", "readingReverse")
            : ("expression", "reading");
        var queries = matchType == "suffix"
            ? termList.Select(YomitanDictionaryImporter.StringReverse).ToList()
            : termList;

        string Condition(string column) => matchType is "prefix" or "suffix"
            ? $"t.{column} >= j.value AND t.{column} < j.value || {PrefixUpperBound}"
            : $"t.{column} = j.value";

        var sql = $"""
            SELECT j.key, 0 AS indexIndex, t.{expressionColumn} AS indexKey, {TermColumns}
            FROM json_each(@items) AS j CROSS JOIN terms AS t ON {Condition(expressionColumn)}
            WHERE +t.dictionary IN (SELECT value FROM json_each(@dictionaries))
            UNION ALL
            SELECT j.key, 1 AS indexIndex, t.{readingColumn} AS indexKey, {TermColumns}
            FROM json_each(@items) AS j CROSS JOIN terms AS t ON {Condition(readingColumn)}
            WHERE +t.dictionary IN (SELECT value FROM json_each(@dictionaries))
            ORDER BY 1, 2, 3, 4
            """;

        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@items", JsonSerializer.Serialize(queries));
        command.Parameters.AddWithValue("@dictionaries", JsonSerializer.Serialize(dictionaries));

        var visited = new HashSet<long>();
        var results = new List<YomitanTermEntryDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt64(3);
            if (!visited.Add(id)) continue;

            var itemIndex = reader.GetInt32(0);
            var matchSourceIsTerm = reader.GetInt32(1) == 0;
            var item = termList[itemIndex];
            // _createTermGeneric promotes prefix and suffix matches to exact when the key equals the query
            var rowKey = matchSourceIsTerm ? reader.GetString(4) : reader.GetString(5);
            var rowMatchType = rowKey == item ? "exact" : matchType;
            results.Add(ReadTerm(reader, 3, itemIndex, matchSourceIsTerm ? "term" : "reading", rowMatchType));
        }
        return results;
    }

    public async Task<IList<YomitanTermEntryDto>> FindTermsExactBulkAsync(IList<YomitanTermExactRequestDto> termList, IList<string> dictionaries, CancellationToken ct = default)
    {
        if (termList.Count == 0 || dictionaries.Count == 0) return [];

        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT j.key, {TermColumns}
            FROM json_each(@items) AS j CROSS JOIN terms AS t ON t.expression = json_extract(j.value, '$[0]')
            WHERE t.reading = json_extract(j.value, '$[1]')
              AND +t.dictionary IN (SELECT value FROM json_each(@dictionaries))
            ORDER BY 1, 2
            """;
        command.Parameters.AddWithValue("@items", JsonSerializer.Serialize(termList.Select(t => new[] { t.Term, t.Reading })));
        command.Parameters.AddWithValue("@dictionaries", JsonSerializer.Serialize(dictionaries));

        var results = new List<YomitanTermEntryDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(ReadTerm(reader, 1, reader.GetInt32(0), "term", "exact"));
        }
        return results;
    }

    public async Task<IList<YomitanTermEntryDto>> FindTermsBySequenceBulkAsync(IList<YomitanSequenceQueryDto> items, CancellationToken ct = default)
    {
        if (items.Count == 0) return [];

        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT j.key, {TermColumns}
            FROM json_each(@items) AS j CROSS JOIN terms AS t
                ON t.dictionary = json_extract(j.value, '$[1]') AND t.sequence = json_extract(j.value, '$[0]')
            ORDER BY 1, 2
            """;
        command.Parameters.AddWithValue("@items", JsonSerializer.Serialize(items.Select(i => new object[] { i.Query, i.Dictionary })));

        var results = new List<YomitanTermEntryDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(ReadTerm(reader, 1, reader.GetInt32(0), "sequence", "exact"));
        }
        return results;
    }

    public async Task<IList<YomitanTermMetaDto>> FindTermMetaBulkAsync(IList<string> termList, IList<string> dictionaries, CancellationToken ct = default)
    {
        if (termList.Count == 0 || dictionaries.Count == 0) return [];

        var results = new List<YomitanTermMetaDto>();
        await foreach (var row in FindMetaAsync("termMeta", "expression", termList, dictionaries, ct))
        {
            results.Add(new YomitanTermMetaDto
            {
                Index = row.Index,
                Term = row.Key,
                Mode = row.Mode,
                Data = ParseJson(row.Data),
                Dictionary = row.Dictionary,
            });
        }
        return results;
    }

    public async Task<IList<YomitanKanjiMetaDto>> FindKanjiMetaBulkAsync(IList<string> kanjiList, IList<string> dictionaries, CancellationToken ct = default)
    {
        if (kanjiList.Count == 0 || dictionaries.Count == 0) return [];

        var results = new List<YomitanKanjiMetaDto>();
        await foreach (var row in FindMetaAsync("kanjiMeta", "character", kanjiList, dictionaries, ct))
        {
            results.Add(new YomitanKanjiMetaDto
            {
                Index = row.Index,
                Character = row.Key,
                Mode = row.Mode,
                Data = ParseJson(row.Data),
                Dictionary = row.Dictionary,
            });
        }
        return results;
    }

    public async Task<IList<YomitanKanjiEntryDto>> FindKanjiBulkAsync(IList<string> kanjiList, IList<string> dictionaries, CancellationToken ct = default)
    {
        if (kanjiList.Count == 0 || dictionaries.Count == 0) return [];

        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT j.key, k.character, k.onyomi, k.kunyomi, k.tags, k.meanings, k.stats, k.dictionary
            FROM json_each(@items) AS j CROSS JOIN kanji AS k ON k.character = j.value
            WHERE +k.dictionary IN (SELECT value FROM json_each(@dictionaries))
            ORDER BY 1, k.id
            """;
        command.Parameters.AddWithValue("@items", JsonSerializer.Serialize(kanjiList));
        command.Parameters.AddWithValue("@dictionaries", JsonSerializer.Serialize(dictionaries));

        var results = new List<YomitanKanjiEntryDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new YomitanKanjiEntryDto
            {
                Index = reader.GetInt32(0),
                Character = reader.GetString(1),
                Onyomi = SplitField(GetNullableString(reader, 2)),
                Kunyomi = SplitField(GetNullableString(reader, 3)),
                Tags = SplitField(GetNullableString(reader, 4)),
                Definitions = ParseJson(reader.GetString(5)),
                Stats = ParseJson(GetNullableString(reader, 6) ?? "{}"),
                Dictionary = reader.GetString(7),
            });
        }
        return results;
    }

    public async Task<IList<YomitanTagDto?>> FindTagMetaBulkAsync(IList<YomitanTagQueryDto> items, CancellationToken ct = default)
    {
        var results = new YomitanTagDto?[items.Count];
        if (items.Count == 0) return results;

        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT j.key, t.name, t.category, t."order", t.notes, t.score, t.dictionary
            FROM json_each(@items) AS j CROSS JOIN tagMeta AS t
                ON t.name = json_extract(j.value, '$[0]') AND +t.dictionary = json_extract(j.value, '$[1]')
            ORDER BY 1, t.id
            """;
        command.Parameters.AddWithValue("@items", JsonSerializer.Serialize(items.Select(i => new[] { i.Query, i.Dictionary })));

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            // _findFirstBulk keeps only the first match per item
            var index = reader.GetInt32(0);
            if (results[index] != null) continue;
            results[index] = new YomitanTagDto
            {
                Name = reader.GetString(1),
                Category = GetNullableString(reader, 2) ?? string.Empty,
                Order = reader.GetDouble(3),
                Notes = GetNullableString(reader, 4) ?? string.Empty,
                Score = reader.GetDouble(5),
                Dictionary = reader.GetString(6),
            };
        }
        return results;
    }

    public async Task<IList<YomitanMediaDto>> GetMediaAsync(IList<YomitanMediaRequestDto> items, CancellationToken ct = default)
    {
        if (items.Count == 0) return [];

        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT j.key, m.dictionary, m.path, m.mediaType, m.width, m.height, m.content
            FROM json_each(@items) AS j CROSS JOIN media AS m
                ON m.path = json_extract(j.value, '$[0]') AND +m.dictionary = json_extract(j.value, '$[1]')
            ORDER BY 1, m.id
            """;
        command.Parameters.AddWithValue("@items", JsonSerializer.Serialize(items.Select(i => new[] { i.Path, i.Dictionary })));

        var results = new List<YomitanMediaDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new YomitanMediaDto
            {
                Index = reader.GetInt32(0),
                Dictionary = reader.GetString(1),
                Path = reader.GetString(2),
                MediaType = reader.GetString(3),
                Width = reader.GetInt32(4),
                Height = reader.GetInt32(5),
                Content = Convert.ToBase64String((byte[]) reader.GetValue(6)),
            });
        }
        return results;
    }

    public async Task<IList<JsonElement>> GetDictionaryInfoAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT summary FROM dictionaries ORDER BY id";

        var results = new List<JsonElement>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(ParseJson(reader.GetString(0)));
        }
        return results;
    }

    public YomitanImportStatusDto GetImportStatus()
    {
        return progress.Snapshot();
    }

    #region Helpers

    private readonly record struct MetaRow(int Index, string Key, string Mode, string Data, string Dictionary);

    private async IAsyncEnumerable<MetaRow> FindMetaAsync(string table, string keyColumn, IList<string> keys, IList<string> dictionaries,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT j.key, m.{keyColumn}, m.mode, m.data, m.dictionary
            FROM json_each(@items) AS j CROSS JOIN {table} AS m ON m.{keyColumn} = j.value
            WHERE +m.dictionary IN (SELECT value FROM json_each(@dictionaries))
            ORDER BY 1, m.id
            """;
        command.Parameters.AddWithValue("@items", JsonSerializer.Serialize(keys));
        command.Parameters.AddWithValue("@dictionaries", JsonSerializer.Serialize(dictionaries));

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            yield return new MetaRow(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
        }
    }

    /// <summary>
    /// Mirrors <c>_createTerm</c>. Columns from <paramref name="offset"/> follow <see cref="TermColumns"/>.
    /// </summary>
    private static YomitanTermEntryDto ReadTerm(SqliteDataReader reader, int offset, int index, string matchSource, string matchType)
    {
        return new YomitanTermEntryDto
        {
            Index = index,
            MatchType = matchType,
            MatchSource = matchSource,
            Term = reader.GetString(offset + 1),
            Reading = reader.GetString(offset + 2),
            DefinitionTags = SplitField(GetNullableString(reader, offset + 3)),
            TermTags = SplitField(GetNullableString(reader, offset + 9)),
            Rules = SplitField(GetNullableString(reader, offset + 4)),
            Definitions = ParseJson(reader.GetString(offset + 6)),
            Score = reader.GetDouble(offset + 5),
            Dictionary = reader.GetString(offset + 7),
            Id = reader.GetInt64(offset),
            Sequence = reader.IsDBNull(offset + 8) ? -1 : reader.GetInt64(offset + 8),
        };
    }

    /// <summary>
    /// Mirrors <c>_splitField</c>
    /// </summary>
    private static IList<string> SplitField(string? field)
    {
        return string.IsNullOrEmpty(field) ? [] : field.Split(' ');
    }

    private static string? GetNullableString(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static JsonElement ParseJson(string json)
    {
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    #endregion
}
