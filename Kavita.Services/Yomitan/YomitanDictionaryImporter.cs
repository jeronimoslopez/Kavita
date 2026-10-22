using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Kavita.Services.Yomitan;

public enum YomitanImportOutcome
{
    Skipped,
    Imported,
    Failed,
}

public sealed record YomitanImportResult(YomitanImportOutcome Outcome, string? Title, string? Revision, string? Error, TimeSpan Elapsed);

/// <summary>
/// Port of the format 3 (and legacy format 1) paths of Yomitan's <c>dictionary-importer.js</c>. Reads a dictionary zip and
/// writes rows shaped like Yomitan's IndexedDB records into <see cref="YomitanDictionaryDatabase"/>.
/// </summary>
public partial class YomitanDictionaryImporter(YomitanDictionaryDatabase database, ILogger<YomitanDictionaryImporter> logger)
{
    private const string IndexFileName = "index.json";
    private const string StylesFileName = "styles.css";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    private static readonly string[] DataTables = ["terms", "termMeta", "kanji", "kanjiMeta", "tagMeta", "media"];

    /// <summary>
    /// Imports the zip unless a dictionary with the same title and revision already imported successfully. Rows from an
    /// older import of the same title, or from the same source file, are deleted first.
    /// </summary>
    /// <param name="zipPath"></param>
    /// <param name="sourceFile">Name that identifies the bundled zip across dictionary updates (titles carry dates)</param>
    /// <param name="progress"></param>
    /// <param name="ct"></param>
    public async Task<YomitanImportResult> ImportIfNeededAsync(string zipPath, string sourceFile, YomitanImportProgress? progress = null, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        string? title = null, revision = null;
        try
        {
            await using var archive = await ZipFile.OpenReadAsync(zipPath, ct);
            var index = ReadIndex(archive);
            title = index.Title;
            revision = index.Revision;

            await using var connection = await database.OpenConnectionAsync(ct);
            var existing = await GetExistingAsync(connection, title, sourceFile, ct);
            var upToDate = existing.FirstOrDefault(e => e.Title == title && e.Revision == revision && e.ImportSuccess);
            if (upToDate != null)
            {
                // Drop stale dictionaries that came from this zip under an older title
                foreach (var staleTitle in existing.Where(e => e.Title != title).Select(e => e.Title).Distinct())
                {
                    await DeleteDictionaryAsync(connection, staleTitle, ct);
                }
                if (upToDate.SourceFile != sourceFile)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = "UPDATE dictionaries SET sourceFile = @source WHERE id = @id";
                    command.Parameters.AddWithValue("@source", sourceFile);
                    command.Parameters.AddWithValue("@id", upToDate.Id);
                    await command.ExecuteNonQueryAsync(ct);
                }
                return new YomitanImportResult(YomitanImportOutcome.Skipped, title, revision, null, stopwatch.Elapsed);
            }

            foreach (var oldTitle in existing.Select(e => e.Title).Append(title).Distinct())
            {
                logger.LogInformation("[Yomitan] Removing previous rows for {Title}", oldTitle);
                await DeleteDictionaryAsync(connection, oldTitle, ct);
            }

            var error = await ImportArchiveAsync(connection, archive, index, sourceFile, progress, ct);
            return new YomitanImportResult(error == null ? YomitanImportOutcome.Imported : YomitanImportOutcome.Failed,
                title, revision, error, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Yomitan] Failed to import {File}", sourceFile);
            return new YomitanImportResult(YomitanImportOutcome.Failed, title, revision, ex.Message, stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// Deletes every row belonging to a dictionary title, mirroring <c>DictionaryDatabase.deleteDictionary</c>
    /// </summary>
    public static async Task DeleteDictionaryAsync(SqliteConnection connection, string title, CancellationToken ct = default)
    {
        await using var transaction = (SqliteTransaction) await connection.BeginTransactionAsync(ct);
        foreach (var table in DataTables)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table} WHERE dictionary = @title";
            command.Parameters.AddWithValue("@title", title);
            await command.ExecuteNonQueryAsync(ct);
        }
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM dictionaries WHERE title = @title";
            command.Parameters.AddWithValue("@title", title);
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    #region Index

    public sealed class DictionaryIndex
    {
        public required JsonElement Root { get; init; }
        public required string Title { get; init; }
        public required string Revision { get; init; }
        public required int Version { get; init; }
    }

    /// <summary>
    /// Mirrors <c>_readAndValidateIndex</c> minus the ajv schema check
    /// </summary>
    public static DictionaryIndex ReadIndex(ZipArchive archive)
    {
        var entry = archive.GetEntry(IndexFileName);
        if (entry == null)
        {
            var nested = archive.Entries.FirstOrDefault(e => e.Name == IndexFileName);
            if (nested != null)
            {
                var redundant = nested.FullName[..^IndexFileName.Length];
                throw new InvalidDataException($"Dictionary index found nested in redundant directories: \"{redundant}\" when it must be in the archive's root directory");
            }
            throw new InvalidDataException("No dictionary index found in archive");
        }

        JsonElement root;
        using (var stream = entry.Open())
        {
            root = JsonSerializer.Deserialize<JsonElement>(stream);
        }
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Dictionary has invalid data in 'index.json'");
        }

        int? version = null;
        if (root.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.Number) version = format.GetInt32();
        else if (root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number) version = v.GetInt32();

        var title = GetString(root, "title");
        var revision = GetString(root, "revision");
        if (version == null || string.IsNullOrEmpty(title) || string.IsNullOrEmpty(revision))
        {
            throw new InvalidDataException("Unrecognized dictionary format");
        }

        return new DictionaryIndex { Root = root, Title = title, Revision = revision, Version = version.Value };
    }

    #endregion

    private sealed record ExistingDictionary(long Id, string Title, string Revision, bool ImportSuccess, string? SourceFile);

    private static async Task<List<ExistingDictionary>> GetExistingAsync(SqliteConnection connection, string title, string sourceFile, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, revision, importSuccess, sourceFile FROM dictionaries WHERE title = @title OR sourceFile = @source ORDER BY id";
        command.Parameters.AddWithValue("@title", title);
        command.Parameters.AddWithValue("@source", sourceFile);
        var result = new List<ExistingDictionary>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new ExistingDictionary(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3) != 0, reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return result;
    }

    /// <summary>
    /// Mirrors <c>importDictionary</c> after the "already imported" check. Returns the error message when the import
    /// failed part way, in which case the summary is stored with importSuccess false like Yomitan does.
    /// </summary>
    private async Task<string?> ImportArchiveAsync(SqliteConnection connection, ZipArchive archive, DictionaryIndex index,
        string sourceFile, YomitanImportProgress? progress, CancellationToken ct)
    {
        var title = index.Title;
        var version = index.Version;

        // Validates isUpdatable and friends before anything is written, like _createSummary does on the first call
        var counts = new SummaryCounts();
        BuildSummaryJson(index, counts, string.Empty, false);

        var termFiles = new List<ZipArchiveEntry>();
        var termMetaFiles = new List<ZipArchiveEntry>();
        var kanjiFiles = new List<ZipArchiveEntry>();
        var kanjiMetaFiles = new List<ZipArchiveEntry>();
        var tagFiles = new List<ZipArchiveEntry>();
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (TermBankRegex().IsMatch(name)) termFiles.Add(entry);
            else if (TermMetaBankRegex().IsMatch(name)) termMetaFiles.Add(entry);
            else if (KanjiBankRegex().IsMatch(name)) kanjiFiles.Add(entry);
            else if (KanjiMetaBankRegex().IsMatch(name)) kanjiMetaFiles.Add(entry);
            else if (TagBankRegex().IsMatch(name)) tagFiles.Add(entry);
        }

        progress?.SetBanks(title, termFiles.Count + termMetaFiles.Count + kanjiFiles.Count + kanjiMetaFiles.Count + tagFiles.Count);
        logger.LogInformation("[Yomitan] Importing {Title} ({Revision}): {Terms} term, {TermMeta} term meta, {Kanji} kanji, {KanjiMeta} kanji meta, {Tags} tag banks",
            title, index.Revision, termFiles.Count, termMetaFiles.Count, kanjiFiles.Count, kanjiMetaFiles.Count, tagFiles.Count);

        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA cache_size = -131072;";
            await pragma.ExecuteNonQueryAsync(ct);
        }

        long dictionaryId;
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO dictionaries (title, revision, version, importSuccess, sourceFile, summary)
                VALUES (@title, @revision, @version, 0, @source, @summary) RETURNING id
                """;
            insert.Parameters.AddWithValue("@title", title);
            insert.Parameters.AddWithValue("@revision", index.Revision);
            insert.Parameters.AddWithValue("@version", version);
            insert.Parameters.AddWithValue("@source", sourceFile);
            insert.Parameters.AddWithValue("@summary", BuildSummaryJson(index, counts, string.Empty, false));
            dictionaryId = (long) (await insert.ExecuteScalarAsync(ct))!;
        }

        var importSuccess = false;
        string? error = null;
        try
        {
            var media = new MediaContext(archive, title);
            foreach (var file in termFiles)
            {
                counts.Terms += await ImportTermBankAsync(connection, file, title, version, media, ct);
                counts.Media = media.Count;
                progress?.BankDone();
            }
            foreach (var file in termMetaFiles)
            {
                await ImportMetaBankAsync(connection, file, title, "termMeta", "expression", counts.TermMeta, ct);
                progress?.BankDone();
            }
            foreach (var file in kanjiFiles)
            {
                counts.Kanji += await ImportKanjiBankAsync(connection, file, title, version, ct);
                progress?.BankDone();
            }
            foreach (var file in kanjiMetaFiles)
            {
                await ImportMetaBankAsync(connection, file, title, "kanjiMeta", "character", counts.KanjiMeta, ct);
                progress?.BankDone();
            }
            foreach (var file in tagFiles)
            {
                counts.TagMeta += await ImportTagBankAsync(connection, file, index, ct);
                progress?.BankDone();
            }
            importSuccess = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Yomitan] Import of {Title} stopped part way", title);
            error = ex.Message;
        }

        var styles = string.Empty;
        var stylesEntry = archive.GetEntry(StylesFileName);
        if (stylesEntry != null)
        {
            using var reader = new StreamReader(await stylesEntry.OpenAsync(ct), Encoding.UTF8);
            styles = await reader.ReadToEndAsync(ct);
        }

        await using (var update = connection.CreateCommand())
        {
            update.CommandText = "UPDATE dictionaries SET summary = @summary, importSuccess = @success WHERE id = @id";
            update.Parameters.AddWithValue("@summary", BuildSummaryJson(index, counts, styles, importSuccess));
            update.Parameters.AddWithValue("@success", importSuccess ? 1 : 0);
            update.Parameters.AddWithValue("@id", dictionaryId);
            await update.ExecuteNonQueryAsync(ct);
        }

        await using (var checkpoint = connection.CreateCommand())
        {
            // Fold the import back into the main file so the WAL doesn't keep hundreds of MB around
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync(ct);
        }

        return error;
    }

    #region Term banks

    private static async Task<int> ImportTermBankAsync(SqliteConnection connection, ZipArchiveEntry file, string dictionary, int version,
        MediaContext media, CancellationToken ct)
    {
        using var document = await ParseBankAsync(file, ct);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return 0;

        await using var transaction = (SqliteTransaction) await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO terms (dictionary, expression, reading, expressionReverse, readingReverse, definitionTags, rules, score, glossary, sequence, termTags)
            VALUES (@dictionary, @expression, @reading, @expressionReverse, @readingReverse, @definitionTags, @rules, @score, @glossary, @sequence, @termTags)
            """;
        var pDictionary = command.Parameters.Add("@dictionary", SqliteType.Text);
        var pExpression = command.Parameters.Add("@expression", SqliteType.Text);
        var pReading = command.Parameters.Add("@reading", SqliteType.Text);
        var pExpressionReverse = command.Parameters.Add("@expressionReverse", SqliteType.Text);
        var pReadingReverse = command.Parameters.Add("@readingReverse", SqliteType.Text);
        var pDefinitionTags = command.Parameters.Add("@definitionTags", SqliteType.Text);
        var pRules = command.Parameters.Add("@rules", SqliteType.Text);
        var pScore = command.Parameters.Add("@score", SqliteType.Real);
        var pGlossary = command.Parameters.Add("@glossary", SqliteType.Text);
        var pSequence = command.Parameters.Add("@sequence", SqliteType.Integer);
        var pTermTags = command.Parameters.Add("@termTags", SqliteType.Text);
        await command.PrepareAsync(ct);

        media.Transaction = transaction;
        pDictionary.Value = dictionary;

        var buffer = new ArrayBufferWriter<byte>(16 * 1024);
        await using var writer = new Utf8JsonWriter(buffer, WriterOptions);

        var count = 0;
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            if (entry.ValueKind != JsonValueKind.Array) continue;

            var expression = GetString(entry, 0) ?? string.Empty;
            var reading = GetString(entry, 1) ?? string.Empty;
            if (reading.Length == 0) reading = expression;

            pExpression.Value = expression;
            pReading.Value = reading;
            pExpressionReverse.Value = StringReverse(expression);
            pReadingReverse.Value = StringReverse(reading);
            pDefinitionTags.Value = (object?) GetString(entry, 2) ?? DBNull.Value;
            pRules.Value = (object?) GetString(entry, 3) ?? DBNull.Value;
            pScore.Value = GetDouble(entry, 4);

            buffer.ResetWrittenCount();
            writer.Reset(buffer);
            var entryInfo = new EntryInfo(expression, reading, dictionary);
            if (version == 1)
            {
                // [expression, reading, definitionTags, rules, score, ...glossary]
                writer.WriteStartArray();
                var i = 0;
                foreach (var item in entry.EnumerateArray())
                {
                    if (i++ < 5) continue;
                    WriteGlossaryItem(writer, item, entryInfo, media);
                }
                writer.WriteEndArray();
                pSequence.Value = DBNull.Value;
                pTermTags.Value = DBNull.Value;
            }
            else
            {
                // [expression, reading, definitionTags, rules, score, glossary, sequence, termTags]
                writer.WriteStartArray();
                if (entry.GetArrayLength() > 5 && entry[5].ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in entry[5].EnumerateArray())
                    {
                        WriteGlossaryItem(writer, item, entryInfo, media);
                    }
                }
                writer.WriteEndArray();
                pSequence.Value = entry.GetArrayLength() > 6 && entry[6].ValueKind == JsonValueKind.Number && entry[6].TryGetInt64(out var sequence)
                    ? sequence
                    : DBNull.Value;
                pTermTags.Value = (object?) GetString(entry, 7) ?? DBNull.Value;
            }
            await writer.FlushAsync(ct);
            pGlossary.Value = Encoding.UTF8.GetString(buffer.WrittenSpan);

            await command.ExecuteNonQueryAsync(ct);
            count++;
        }

        await transaction.CommitAsync(ct);
        media.Transaction = null;
        return count;
    }

    private readonly record struct EntryInfo(string Expression, string Reading, string Dictionary);

    /// <summary>
    /// Mirrors the "Extended data support" loop: strings and deinflection tuples pass through, objects go to
    /// <c>_formatDictionaryTermGlossaryObject</c>
    /// </summary>
    private static void WriteGlossaryItem(Utf8JsonWriter writer, JsonElement item, EntryInfo entry, MediaContext media)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            item.WriteTo(writer);
            return;
        }

        var type = GetString(item, "type");
        switch (type)
        {
            case "text":
                writer.WriteStringValue(GetString(item, "text"));
                break;
            case "image":
                WriteImage(writer, item, false, entry, media);
                break;
            case "structured-content":
                writer.WriteStartObject();
                writer.WriteString("type", "structured-content");
                if (item.TryGetProperty("content", out var content))
                {
                    writer.WritePropertyName("content");
                    WriteStructuredContent(writer, content, entry, media);
                }
                writer.WriteEndObject();
                break;
            default:
                throw new InvalidDataException($"Unhandled data type: {type}");
        }
    }

    /// <summary>
    /// Mirrors <c>_prepareStructuredContent</c>
    /// </summary>
    private static void WriteStructuredContent(Utf8JsonWriter writer, JsonElement content, EntryInfo entry, MediaContext media)
    {
        switch (content.ValueKind)
        {
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var child in content.EnumerateArray())
                {
                    WriteStructuredContent(writer, child, entry, media);
                }
                writer.WriteEndArray();
                return;
            case JsonValueKind.Object:
                if (GetString(content, "tag") == "img")
                {
                    WriteImage(writer, content, true, entry, media);
                    return;
                }
                writer.WriteStartObject();
                foreach (var property in content.EnumerateObject())
                {
                    if (property.NameEquals("content"))
                    {
                        writer.WritePropertyName("content");
                        WriteStructuredContent(writer, property.Value, entry, media);
                    }
                    else
                    {
                        property.WriteTo(writer);
                    }
                }
                writer.WriteEndObject();
                return;
            default:
                content.WriteTo(writer);
                return;
        }
    }

    /// <summary>
    /// Mirrors <c>_createImageData</c> (and <c>_resolveStructuredContentImage</c> for structured content). The source
    /// width and height become preferredWidth and preferredHeight, while width and height are the image's real size.
    /// </summary>
    private static void WriteImage(Utf8JsonWriter writer, JsonElement source, bool structured, EntryInfo entry, MediaContext media)
    {
        var path = GetString(source, "path") ?? string.Empty;
        var (width, height) = media.GetImageMedia(path, entry);

        writer.WriteStartObject();
        if (structured) writer.WriteString("tag", "img");
        else writer.WriteString("type", "image");
        writer.WriteString("path", path);
        writer.WriteNumber("width", width);
        writer.WriteNumber("height", height);
        CopyIfKind(writer, source, "width", "preferredWidth", JsonValueKind.Number);
        CopyIfKind(writer, source, "height", "preferredHeight", JsonValueKind.Number);
        CopyIfKind(writer, source, "title", "title", JsonValueKind.String);
        CopyIfKind(writer, source, "alt", "alt", JsonValueKind.String);
        CopyIfKind(writer, source, "description", "description", JsonValueKind.String);
        CopyIfBool(writer, source, "pixelated");
        CopyIfKind(writer, source, "imageRendering", "imageRendering", JsonValueKind.String);
        CopyIfKind(writer, source, "appearance", "appearance", JsonValueKind.String);
        CopyIfBool(writer, source, "background");
        CopyIfBool(writer, source, "collapsed");
        CopyIfBool(writer, source, "collapsible");
        if (structured)
        {
            CopyIfKind(writer, source, "verticalAlign", "verticalAlign", JsonValueKind.String);
            CopyIfKind(writer, source, "border", "border", JsonValueKind.String);
            CopyIfKind(writer, source, "borderRadius", "borderRadius", JsonValueKind.String);
            CopyIfKind(writer, source, "sizeUnits", "sizeUnits", JsonValueKind.String);
        }
        writer.WriteEndObject();
    }

    private static void CopyIfKind(Utf8JsonWriter writer, JsonElement source, string from, string to, JsonValueKind kind)
    {
        if (!source.TryGetProperty(from, out var value) || value.ValueKind != kind) return;
        writer.WritePropertyName(to);
        value.WriteTo(writer);
    }

    private static void CopyIfBool(Utf8JsonWriter writer, JsonElement source, string name)
    {
        if (!source.TryGetProperty(name, out var value)) return;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            writer.WriteBoolean(name, value.GetBoolean());
        }
    }

    /// <summary>
    /// Media loaded for one dictionary. Each path is stored once per dictionary like Yomitan's uniqueMediaPaths.
    /// </summary>
    private sealed class MediaContext(ZipArchive archive, string dictionary)
    {
        private readonly Dictionary<string, (int Width, int Height)> _loaded = new(StringComparer.Ordinal);
        private Dictionary<string, ZipArchiveEntry>? _fileMap;
        private SqliteCommand? _insert;

        public SqliteTransaction? Transaction { get; set; }
        public int Count => _loaded.Count;

        /// <summary>
        /// Mirrors <c>_getImageMedia</c>
        /// </summary>
        public (int Width, int Height) GetImageMedia(string path, EntryInfo entry)
        {
            if (_loaded.TryGetValue(path, out var known)) return known;

            _fileMap ??= archive.Entries.GroupBy(e => e.FullName, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            if (!_fileMap.TryGetValue(path, out var file))
            {
                throw CreateError("Could not find image", path, entry);
            }

            var mediaType = YomitanMediaUtil.GetImageMediaTypeFromFileName(path)
                            ?? throw CreateError("Could not determine media type for image", path, entry);

            byte[] content;
            using (var stream = file.Open())
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                content = memory.ToArray();
            }

            int width, height;
            try
            {
                (width, height) = YomitanMediaUtil.GetImageDimensions(content, mediaType);
            }
            catch (Exception)
            {
                throw CreateError("Could not load image", path, entry);
            }

            Insert(path, mediaType, width, height, content);
            _loaded[path] = (width, height);
            return (width, height);
        }

        private void Insert(string path, string mediaType, int width, int height, byte[] content)
        {
            if (Transaction == null) throw new InvalidOperationException("Media insert outside a transaction");
            if (_insert == null || _insert.Transaction != Transaction)
            {
                _insert?.Dispose();
                _insert = Transaction.Connection!.CreateCommand();
                _insert.Transaction = Transaction;
                _insert.CommandText = "INSERT INTO media (dictionary, path, mediaType, width, height, content) VALUES (@dictionary, @path, @mediaType, @width, @height, @content)";
                _insert.Parameters.Add("@dictionary", SqliteType.Text);
                _insert.Parameters.Add("@path", SqliteType.Text);
                _insert.Parameters.Add("@mediaType", SqliteType.Text);
                _insert.Parameters.Add("@width", SqliteType.Integer);
                _insert.Parameters.Add("@height", SqliteType.Integer);
                _insert.Parameters.Add("@content", SqliteType.Blob);
            }
            _insert.Parameters["@dictionary"].Value = dictionary;
            _insert.Parameters["@path"].Value = path;
            _insert.Parameters["@mediaType"].Value = mediaType;
            _insert.Parameters["@width"].Value = width;
            _insert.Parameters["@height"].Value = height;
            _insert.Parameters["@content"].Value = content;
            _insert.ExecuteNonQuery();
        }

        private static InvalidDataException CreateError(string message, string path, EntryInfo entry)
        {
            var readingSource = entry.Reading.Length > 0 ? $" ({entry.Reading})" : string.Empty;
            return new InvalidDataException($"{message} at path {JsonSerializer.Serialize(path)} for {entry.Expression}{readingSource} in {entry.Dictionary}");
        }
    }

    #endregion

    #region Meta, kanji and tag banks

    /// <summary>
    /// term_meta_bank and kanji_meta_bank rows are [key, mode, data]
    /// </summary>
    private static async Task ImportMetaBankAsync(SqliteConnection connection, ZipArchiveEntry file, string dictionary,
        string table, string keyColumn, Dictionary<string, long> counts, CancellationToken ct)
    {
        using var document = await ParseBankAsync(file, ct);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return;

        await using var transaction = (SqliteTransaction) await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO {table} (dictionary, {keyColumn}, mode, data) VALUES (@dictionary, @key, @mode, @data)";
        var pDictionary = command.Parameters.Add("@dictionary", SqliteType.Text);
        var pKey = command.Parameters.Add("@key", SqliteType.Text);
        var pMode = command.Parameters.Add("@mode", SqliteType.Text);
        var pData = command.Parameters.Add("@data", SqliteType.Text);
        await command.PrepareAsync(ct);
        pDictionary.Value = dictionary;

        // Mirrors _getMetaCounts: total first, then each mode in first-seen order
        var bankCounts = new Dictionary<string, long>();
        var total = 0L;
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            if (entry.ValueKind != JsonValueKind.Array) continue;
            var mode = GetString(entry, 1) ?? string.Empty;
            pKey.Value = GetString(entry, 0) ?? string.Empty;
            pMode.Value = mode;
            pData.Value = entry.GetArrayLength() > 2 ? entry[2].GetRawText() : "null";
            await command.ExecuteNonQueryAsync(ct);

            total++;
            bankCounts[mode] = bankCounts.GetValueOrDefault(mode) + 1;
        }
        await transaction.CommitAsync(ct);

        counts["total"] = counts.GetValueOrDefault("total") + total;
        foreach (var (mode, value) in bankCounts)
        {
            if (mode == "total") continue;
            counts[mode] = counts.GetValueOrDefault(mode) + value;
        }
    }

    private static async Task<int> ImportKanjiBankAsync(SqliteConnection connection, ZipArchiveEntry file, string dictionary, int version, CancellationToken ct)
    {
        using var document = await ParseBankAsync(file, ct);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return 0;

        await using var transaction = (SqliteTransaction) await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO kanji (dictionary, character, onyomi, kunyomi, tags, meanings, stats)
            VALUES (@dictionary, @character, @onyomi, @kunyomi, @tags, @meanings, @stats)
            """;
        var pDictionary = command.Parameters.Add("@dictionary", SqliteType.Text);
        var pCharacter = command.Parameters.Add("@character", SqliteType.Text);
        var pOnyomi = command.Parameters.Add("@onyomi", SqliteType.Text);
        var pKunyomi = command.Parameters.Add("@kunyomi", SqliteType.Text);
        var pTags = command.Parameters.Add("@tags", SqliteType.Text);
        var pMeanings = command.Parameters.Add("@meanings", SqliteType.Text);
        var pStats = command.Parameters.Add("@stats", SqliteType.Text);
        await command.PrepareAsync(ct);
        pDictionary.Value = dictionary;

        var buffer = new ArrayBufferWriter<byte>(1024);
        await using var writer = new Utf8JsonWriter(buffer, WriterOptions);

        var count = 0;
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            if (entry.ValueKind != JsonValueKind.Array) continue;
            pCharacter.Value = GetString(entry, 0) ?? string.Empty;
            pOnyomi.Value = (object?) GetString(entry, 1) ?? DBNull.Value;
            pKunyomi.Value = (object?) GetString(entry, 2) ?? DBNull.Value;
            pTags.Value = (object?) GetString(entry, 3) ?? DBNull.Value;

            if (version == 1)
            {
                // [character, onyomi, kunyomi, tags, ...meanings]
                buffer.ResetWrittenCount();
                writer.Reset(buffer);
                writer.WriteStartArray();
                var i = 0;
                foreach (var item in entry.EnumerateArray())
                {
                    if (i++ < 4) continue;
                    item.WriteTo(writer);
                }
                writer.WriteEndArray();
                await writer.FlushAsync(ct);
                pMeanings.Value = Encoding.UTF8.GetString(buffer.WrittenSpan);
                pStats.Value = DBNull.Value;
            }
            else
            {
                // [character, onyomi, kunyomi, tags, meanings, stats]
                pMeanings.Value = entry.GetArrayLength() > 4 ? entry[4].GetRawText() : "[]";
                pStats.Value = entry.GetArrayLength() > 5 && entry[5].ValueKind == JsonValueKind.Object ? entry[5].GetRawText() : DBNull.Value;
            }

            await command.ExecuteNonQueryAsync(ct);
            count++;
        }
        await transaction.CommitAsync(ct);
        return count;
    }

    private static async Task<int> ImportTagBankAsync(SqliteConnection connection, ZipArchiveEntry file, DictionaryIndex index, CancellationToken ct)
    {
        using var document = await ParseBankAsync(file, ct);

        // [name, category, order, notes, score], plus the legacy index.json tagMeta appended per tag bank by _addOldIndexTags
        var tags = new List<(string Name, string? Category, double Order, string? Notes, double Score)>();
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Array) continue;
                tags.Add((GetString(entry, 0) ?? string.Empty, GetString(entry, 1), GetDouble(entry, 2), GetString(entry, 3), GetDouble(entry, 4)));
            }
        }
        if (index.Root.TryGetProperty("tagMeta", out var tagMeta) && tagMeta.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in tagMeta.EnumerateObject())
            {
                var value = property.Value;
                if (value.ValueKind != JsonValueKind.Object) continue;
                tags.Add((property.Name, GetString(value, "category"), GetDouble(value, "order"), GetString(value, "notes"), GetDouble(value, "score")));
            }
        }

        await using var transaction = (SqliteTransaction) await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO tagMeta (dictionary, name, category, "order", notes, score)
            VALUES (@dictionary, @name, @category, @order, @notes, @score)
            """;
        var pDictionary = command.Parameters.Add("@dictionary", SqliteType.Text);
        var pName = command.Parameters.Add("@name", SqliteType.Text);
        var pCategory = command.Parameters.Add("@category", SqliteType.Text);
        var pOrder = command.Parameters.Add("@order", SqliteType.Real);
        var pNotes = command.Parameters.Add("@notes", SqliteType.Text);
        var pScore = command.Parameters.Add("@score", SqliteType.Real);
        await command.PrepareAsync(ct);
        pDictionary.Value = index.Title;

        foreach (var tag in tags)
        {
            pName.Value = tag.Name;
            pCategory.Value = (object?) tag.Category ?? DBNull.Value;
            pOrder.Value = tag.Order;
            pNotes.Value = (object?) tag.Notes ?? DBNull.Value;
            pScore.Value = tag.Score;
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return tags.Count;
    }

    #endregion

    #region Summary

    private sealed class SummaryCounts
    {
        public long Terms { get; set; }
        public Dictionary<string, long> TermMeta { get; } = new() { ["total"] = 0 };
        public long Kanji { get; set; }
        public Dictionary<string, long> KanjiMeta { get; } = new() { ["total"] = 0 };
        public long TagMeta { get; set; }
        public long Media { get; set; }
    }

    /// <summary>
    /// Mirrors <c>_createSummary</c> property for property, in the same order
    /// </summary>
    private static string BuildSummaryJson(DictionaryIndex index, SummaryCounts counts, string styles, bool importSuccess)
    {
        var root = index.Root;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("title", index.Title);
            writer.WriteString("revision", index.Revision);
            writer.WriteBoolean("sequenced", root.TryGetProperty("sequenced", out var sequenced) && sequenced.ValueKind == JsonValueKind.True);
            writer.WriteNumber("version", index.Version);
            writer.WriteNumber("importDate", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            // Reversed columns are always stored, so suffix (prefix wildcard) searches always work
            writer.WriteBoolean("prefixWildcardsSupported", true);

            writer.WriteStartObject("counts");
            writer.WriteStartObject("terms");
            writer.WriteNumber("total", counts.Terms);
            writer.WriteEndObject();
            WriteMetaCounts(writer, "termMeta", counts.TermMeta);
            writer.WriteStartObject("kanji");
            writer.WriteNumber("total", counts.Kanji);
            writer.WriteEndObject();
            WriteMetaCounts(writer, "kanjiMeta", counts.KanjiMeta);
            writer.WriteStartObject("tagMeta");
            writer.WriteNumber("total", counts.TagMeta);
            writer.WriteEndObject();
            writer.WriteStartObject("media");
            writer.WriteNumber("total", counts.Media);
            writer.WriteEndObject();
            writer.WriteEndObject();

            writer.WriteString("styles", styles);
            writer.WriteBoolean("importSuccess", importSuccess);

            // Kavita runs the dictionaries it bundles, so the minimum version check from _createSummary is skipped
            CopyIfKind(writer, root, "minimumYomitanVersion", "minimumYomitanVersion", JsonValueKind.String);
            CopyIfKind(writer, root, "author", "author", JsonValueKind.String);
            CopyIfKind(writer, root, "url", "url", JsonValueKind.String);
            CopyIfKind(writer, root, "description", "description", JsonValueKind.String);
            CopyIfKind(writer, root, "attribution", "attribution", JsonValueKind.String);
            CopyIfKind(writer, root, "frequencyMode", "frequencyMode", JsonValueKind.String);
            CopyIfKind(writer, root, "sourceLanguage", "sourceLanguage", JsonValueKind.String);
            CopyIfKind(writer, root, "targetLanguage", "targetLanguage", JsonValueKind.String);
            if (root.TryGetProperty("isUpdatable", out var isUpdatable) && isUpdatable.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                var indexUrl = GetString(root, "indexUrl");
                var downloadUrl = GetString(root, "downloadUrl");
                if (!isUpdatable.GetBoolean() || !IsHttpUrl(indexUrl) || !IsHttpUrl(downloadUrl))
                {
                    throw new InvalidDataException("Invalid index data for updatable dictionary");
                }
                writer.WriteBoolean("isUpdatable", true);
                writer.WriteString("indexUrl", indexUrl);
                writer.WriteString("downloadUrl", downloadUrl);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteMetaCounts(Utf8JsonWriter writer, string name, Dictionary<string, long> counts)
    {
        writer.WriteStartObject(name);
        foreach (var (key, value) in counts)
        {
            writer.WriteNumber(key, value);
        }
        writer.WriteEndObject();
    }

    private static bool IsHttpUrl(string? value)
    {
        return value != null && Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    #endregion

    #region Helpers

    private static async Task<JsonDocument> ParseBankAsync(ZipArchiveEntry file, CancellationToken ct)
    {
        try
        {
            await using var stream = await file.OpenAsync(ct);
            return await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 512 }, ct);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{ex.Message} in '{file.FullName}'", ex);
        }
    }

    /// <summary>
    /// Mirrors Yomitan's <c>stringReverse</c>, which reverses by code point so surrogate pairs stay intact
    /// </summary>
    public static string StringReverse(string value)
    {
        if (value.Length < 2) return value;
        var result = new char[value.Length];
        var write = value.Length;
        var i = 0;
        while (i < value.Length)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                write -= 2;
                result[write] = value[i];
                result[write + 1] = value[i + 1];
                i += 2;
            }
            else
            {
                result[--write] = value[i];
                i++;
            }
        }
        return new string(result);
    }

    private static string? GetString(JsonElement array, int index)
    {
        if (array.GetArrayLength() <= index) return null;
        var value = array[index];
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static string? GetString(JsonElement obj, string name)
    {
        return obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static double GetDouble(JsonElement array, int index)
    {
        if (array.GetArrayLength() <= index) return 0;
        var value = array[index];
        return value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;
    }

    private static double GetDouble(JsonElement obj, string name)
    {
        return obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;
    }

    [GeneratedRegex(@"^term_bank_(\d+)\.json$")]
    private static partial Regex TermBankRegex();
    [GeneratedRegex(@"^term_meta_bank_(\d+)\.json$")]
    private static partial Regex TermMetaBankRegex();
    [GeneratedRegex(@"^kanji_bank_(\d+)\.json$")]
    private static partial Regex KanjiBankRegex();
    [GeneratedRegex(@"^kanji_meta_bank_(\d+)\.json$")]
    private static partial Regex KanjiMetaBankRegex();
    [GeneratedRegex(@"^tag_bank_(\d+)\.json$")]
    private static partial Regex TagBankRegex();

    #endregion
}
