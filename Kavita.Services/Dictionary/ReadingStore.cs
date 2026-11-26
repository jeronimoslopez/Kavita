using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Kavita.API.Services;
using Kavita.Models.DTOs.Dictionary;
using Microsoft.Data.Sqlite;

namespace Kavita.Services.Dictionary;

/// <remarks>
/// Every method opens its own pooled connection, so scoped services and the tokenize worker can call the singleton at
/// once. Writes run in BEGIN IMMEDIATE transactions, which take SQLite's write lock before the first read, so a
/// read-then-write can't interleave with another writer
/// </remarks>
public class ReadingStore(ReadingDatabase database, TimeProvider? timeProvider = null) : IReadingStore
{
    /// <summary>
    /// The first unit in queue order in one state, pending or refresh. Taking each state's front row on its own keeps
    /// both reads on the front of ix_unit_queue, where one query over both states would sort them. Public so the tests
    /// can check that it reads ix_unit_queue
    /// </summary>
    public const string NextPendingUnitSql = """
        SELECT id, kind, manga_file_id, page, source_stamp, attempts, priority, requested_utc
        FROM unit
        WHERE state = $state
        ORDER BY priority DESC, requested_utc, id
        LIMIT 1
        """;

    /// <summary>
    /// Pending analysis rows in queue order. Public so the tests can check that it reads ix_analysis_queue
    /// </summary>
    public const string PendingAnalysisSql = """
        SELECT a.id, a.unit_id, u.manga_file_id, u.page, a.block, a.s, a.e, a.word_id, a.priority
        FROM analysis a
        JOIN unit u ON u.id = a.unit_id
        WHERE a.state = 'pending' AND a.priority >= $minPriority
        ORDER BY a.priority DESC, a.requested_utc, a.id
        LIMIT $limit
        """;

    /// <summary>
    /// Raises one page's or one file's pending units. The unary plus keeps the planner off ix_unit_queue, whose
    /// pending range covers every queued page of every book, so the update reads only the file's rows through the
    /// (kind, manga_file_id, page) key. Public so the tests can check the plan
    /// </summary>
    public const string RaiseUnitPrioritySql = """
        UPDATE unit SET priority = $priority, requested_utc = $now
        WHERE kind = $kind AND manga_file_id = $file AND ($page IS NULL OR page = $page)
          AND +state IN ('pending', 'refresh') AND +priority < $priority
        """;

    /// <summary>
    /// Queues a file's done units whose blocks came from another sidecar version. A unit saved with no version has no
    /// blocks, so tokenizing it again can't change anything. The unary plus keeps the planner on the
    /// (kind, manga_file_id, page) key for the same reason as <see cref="RaiseUnitPrioritySql"/>. Public so the tests
    /// can check the plan
    /// </summary>
    public const string RefreshUnitsSql = """
        UPDATE unit SET state = 'refresh', attempts = 0, priority = MAX(priority, $priority), requested_utc = $now
        WHERE kind = $kind AND manga_file_id = $file AND +state = 'done'
          AND tokenizer_version IS NOT NULL AND tokenizer_version <> '' AND tokenizer_version <> $version
        """;

    /// <summary>
    /// Raises the pending analysis rows of one page, one block or one file. The unary plus keeps the planner off
    /// ix_analysis_queue for the same reason as <see cref="RaiseUnitPrioritySql"/>, so rows are found by unit_id.
    /// Public so the tests can check the plan
    /// </summary>
    public const string RaiseAnalysisPrioritySql = """
        UPDATE analysis SET priority = $priority, requested_utc = $now
        WHERE +state = 'pending' AND +priority < $priority
          AND unit_id IN (
              SELECT id FROM unit WHERE kind = $kind AND manga_file_id = $file AND ($page IS NULL OR page = $page))
          AND ($page IS NULL OR $block IS NULL OR block = $block)
        """;

    /// <summary>
    /// <see cref="SidecarWordDto.IsAmbiguous"/> as a condition on a word row, which stores readings and senses as JSON
    /// arrays. It's the only place the store decides which words get analysis rows
    /// </summary>
    public const string AmbiguousWord = "(json_array_length(senses) > 1 OR json_array_length(readings) > 1)";

    /// <summary>
    /// The marked readings of some words over the units of some files, summed per word, reading and printed form.
    /// It reads the files' units through unit's (kind, manga_file_id, page) key and each unit's rows for the words
    /// through reading_mark's key. Public so the tests can check the plan
    /// </summary>
    /// <param name="files">The IN list of file id parameters</param>
    /// <param name="words">The IN list of word id parameters</param>
    public static string ReadingMarkTotalsSql(string files, string words) => $"""
        SELECT m.word_id, m.reading, m.printed, SUM(m.count)
        FROM unit u
        JOIN reading_mark m ON m.unit_id = u.id
        WHERE u.kind = $kind AND u.manga_file_id IN ({files}) AND m.word_id IN ({words})
        GROUP BY m.word_id, m.reading, m.printed
        """;

    /// <summary>
    /// The uses of some words over the units of some files, summed per word, read the way
    /// <see cref="ReadingMarkTotalsSql"/> reads. Public so the tests can check the plan
    /// </summary>
    public static string WordUseTotalsSql(string files, string words) => $"""
        SELECT w.word_id, SUM(w.uses)
        FROM unit u
        JOIN word_use w ON w.unit_id = u.id
        WHERE u.kind = $kind AND u.manga_file_id IN ({files}) AND w.word_id IN ({words})
        GROUP BY w.word_id
        """;

    /// <summary>
    /// Ids per IN list. Older SQLite builds cap a statement at 999 parameters
    /// </summary>
    private const int BatchSize = 500;

    /// <summary>
    /// File ids and word ids per IN list when reading marks. A statement holds one list of each, so both together stay
    /// under 999 parameters
    /// </summary>
    private const int MarkBatchSize = 400;

    /// <summary>
    /// Units whose blocks <see cref="RequestAnalysisAsync"/> holds in memory at once. A page's blocks run to a few
    /// hundred KB of JSON, so this keeps a request for a long book to a few MB
    /// </summary>
    private const int UnitsPerRequestBatch = 16;

    private const string Epub = "epub";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<long>> UpsertWordsAsync(IReadOnlyList<SidecarWordDto> words, string lexiconVersion,
        CancellationToken ct = default)
    {
        if (words.Count == 0) return [];

        await using var connection = await database.OpenConnectionAsync(ct);
        await using var transaction = BeginWrite(connection);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO word (lemma, pos, is_proper, reading, jlpt, readings, senses, lexicon_version)
            VALUES ($lemma, $pos, $isProper, $reading, $jlpt, $readings, $senses, $lexiconVersion)
            ON CONFLICT (lemma, pos, is_proper) DO UPDATE SET
                reading = excluded.reading,
                jlpt = excluded.jlpt,
                readings = excluded.readings,
                senses = excluded.senses,
                lexicon_version = excluded.lexicon_version
            RETURNING id
            """;
        var lemma = command.Parameters.Add("$lemma", SqliteType.Text);
        var pos = command.Parameters.Add("$pos", SqliteType.Text);
        var isProper = command.Parameters.Add("$isProper", SqliteType.Integer);
        var reading = command.Parameters.Add("$reading", SqliteType.Text);
        var jlpt = command.Parameters.Add("$jlpt", SqliteType.Integer);
        var readings = command.Parameters.Add("$readings", SqliteType.Text);
        var senses = command.Parameters.Add("$senses", SqliteType.Text);
        command.Parameters.AddWithValue("$lexiconVersion", lexiconVersion);

        var ids = new List<long>(words.Count);
        foreach (var word in words)
        {
            lemma.Value = word.Lemma;
            pos.Value = word.Pos;
            isProper.Value = word.IsProper ? 1 : 0;
            reading.Value = word.Reading;
            jlpt.Value = (object?) word.Jlpt ?? DBNull.Value;
            readings.Value = JsonSerializer.Serialize(word.Readings, ReadingJson.Options);
            senses.Value = JsonSerializer.Serialize(word.Senses, ReadingJson.Options);
            ids.Add(Convert.ToInt64(await command.ExecuteScalarAsync(ct)));
        }

        await transaction.CommitAsync(ct);
        return ids;
    }

    public async Task<IReadOnlyDictionary<long, StoredWord>> GetWordsAsync(IReadOnlyCollection<long> ids,
        CancellationToken ct = default)
    {
        var result = new Dictionary<long, StoredWord>();
        if (ids.Count == 0) return result;

        await using var connection = await database.OpenConnectionAsync(ct);
        await ReadWordsAsync(connection, null, ids, result, ct);
        return result;
    }

    public async Task<IReadOnlyDictionary<int, string>> GetFileStampsAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        // Seeding gives every unit of a file the same stamp, so any one of them stands for the file
        command.CommandText = """
            SELECT manga_file_id, MIN(source_stamp) FROM unit WHERE kind = $kind GROUP BY manga_file_id
            """;
        command.Parameters.AddWithValue("$kind", Epub);

        var result = new Dictionary<int, string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[reader.GetInt32(0)] = reader.GetString(1);
        }
        return result;
    }

    public async Task SeedFileAsync(int mangaFileId, int pageCount, string sourceStamp, int priority,
        CancellationToken ct = default)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var transaction = BeginWrite(connection);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$kind", Epub);
        command.Parameters.AddWithValue("$file", mangaFileId);
        command.Parameters.AddWithValue("$pageCount", pageCount);
        command.Parameters.AddWithValue("$stamp", sourceStamp);
        command.Parameters.AddWithValue("$priority", priority);
        command.Parameters.AddWithValue("$now", Now());

        // The recursive CTE yields pages 0 to pageCount - 1, and none when pageCount is 0 or less. WHERE true is how
        // SQLite tells an upsert's ON CONFLICT apart from a join's ON
        command.CommandText = """
            DELETE FROM analysis WHERE unit_id IN (
                SELECT id FROM unit WHERE kind = $kind AND manga_file_id = $file AND page >= $pageCount);
            DELETE FROM reading_mark WHERE unit_id IN (
                SELECT id FROM unit WHERE kind = $kind AND manga_file_id = $file AND page >= $pageCount);
            DELETE FROM word_use WHERE unit_id IN (
                SELECT id FROM unit WHERE kind = $kind AND manga_file_id = $file AND page >= $pageCount);
            DELETE FROM unit WHERE kind = $kind AND manga_file_id = $file AND page >= $pageCount;

            UPDATE unit
            SET source_stamp = $stamp, state = 'pending', attempts = 0, priority = $priority, requested_utc = $now
            WHERE kind = $kind AND manga_file_id = $file AND source_stamp <> $stamp;

            WITH RECURSIVE pages(n) AS (
                SELECT 0 WHERE 0 < $pageCount
                UNION ALL
                SELECT n + 1 FROM pages WHERE n + 1 < $pageCount)
            INSERT INTO unit (kind, manga_file_id, page, source_stamp, state, attempts, priority, requested_utc)
            SELECT $kind, $file, n, $stamp, 'pending', 0, $priority, $now FROM pages WHERE true
            ON CONFLICT (kind, manga_file_id, page) DO NOTHING;
            """;
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task DeleteFilesAsync(IReadOnlyCollection<int> mangaFileIds, CancellationToken ct = default)
    {
        if (mangaFileIds.Count == 0) return;

        await using var connection = await database.OpenConnectionAsync(ct);
        await using var transaction = BeginWrite(connection);
        foreach (var batch in mangaFileIds.Distinct().Chunk(BatchSize))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$kind", Epub);
            var files = AddInList(command, "f", batch);
            command.CommandText = $"""
                DELETE FROM analysis WHERE unit_id IN (
                    SELECT id FROM unit WHERE kind = $kind AND manga_file_id IN ({files}));
                DELETE FROM reading_mark WHERE unit_id IN (
                    SELECT id FROM unit WHERE kind = $kind AND manga_file_id IN ({files}));
                DELETE FROM word_use WHERE unit_id IN (
                    SELECT id FROM unit WHERE kind = $kind AND manga_file_id IN ({files}));
                DELETE FROM unit WHERE kind = $kind AND manga_file_id IN ({files});
                DELETE FROM analysis_request WHERE manga_file_id IN ({files});
                """;
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    public async Task<ReadingUnitRef?> GetNextPendingUnitAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        // One snapshot for both reads, so a unit moving between the states can't be read twice or missed
        await using var snapshot = connection.BeginTransaction(deferred: true);
        await using var command = connection.CreateCommand();
        command.Transaction = snapshot;
        command.CommandText = NextPendingUnitSql;
        var state = command.Parameters.Add("$state", SqliteType.Text);

        (ReadingUnitRef Unit, int Priority, long RequestedUtc)? best = null;
        foreach (var name in new[] { Name(ReadingUnitState.Pending), Name(ReadingUnitState.Refresh) })
        {
            state.Value = name;
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) continue;

            var unit = new ReadingUnitRef(reader.GetInt64(0), ParseKind(reader.GetString(1)), reader.GetInt32(2),
                reader.GetInt32(3), reader.GetString(4), reader.GetInt32(5));
            var candidate = (unit, reader.GetInt32(6), reader.GetInt64(7));
            if (best == null || QueueOrder(candidate, best.Value) < 0) best = candidate;
        }
        return best?.Unit;
    }

    /// <returns>Below 0 when <paramref name="a"/> comes first in the queue order IReadingStore documents</returns>
    private static int QueueOrder((ReadingUnitRef Unit, int Priority, long RequestedUtc) a,
        (ReadingUnitRef Unit, int Priority, long RequestedUtc) b)
    {
        if (a.Priority != b.Priority) return b.Priority.CompareTo(a.Priority);
        if (a.RequestedUtc != b.RequestedUtc) return a.RequestedUtc.CompareTo(b.RequestedUtc);
        return a.Unit.Id.CompareTo(b.Unit.Id);
    }

    public async Task<int> RefreshUnitsAsync(int mangaFileId, string tokenizerVersion, int priority,
        CancellationToken ct = default)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = RefreshUnitsSql;
        command.Parameters.AddWithValue("$kind", Epub);
        command.Parameters.AddWithValue("$file", mangaFileId);
        command.Parameters.AddWithValue("$version", tokenizerVersion);
        command.Parameters.AddWithValue("$priority", priority);
        command.Parameters.AddWithValue("$now", Now());
        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> SaveTokenizedUnitAsync(long unitId, string sourceStamp, string tokenizerVersion,
        IReadOnlyList<StoredBlock> blocks, CancellationToken ct = default)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var transaction = BeginWrite(connection);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$unit", unitId);

        command.CommandText = "SELECT manga_file_id, source_stamp FROM unit WHERE id = $unit";
        int mangaFileId;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct) || reader.GetString(1) != sourceStamp) return false;
            mangaFileId = reader.GetInt32(0);
        }

        command.CommandText = """
            UPDATE unit SET state = 'done', tokenizer_version = $version, blocks = $blocks WHERE id = $unit
            """;
        command.Parameters.AddWithValue("$version", tokenizerVersion);
        command.Parameters.Add("$blocks", SqliteType.Blob).Value = ReadingBlocks.Encode(blocks);
        await command.ExecuteNonQueryAsync(ct);

        await ReplaceMarksAsync(connection, transaction, unitId, blocks, ct);

        command.CommandText = "SELECT priority FROM analysis_request WHERE manga_file_id = $file";
        command.Parameters.AddWithValue("$file", mangaFileId);
        var requestPriority = await command.ExecuteScalarAsync(ct) is { } p ? Convert.ToInt32(p) : (int?) null;

        if (requestPriority == null)
        {
            command.CommandText = "DELETE FROM analysis WHERE unit_id = $unit";
            await command.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
            return true;
        }

        var ambiguousWords = await AmbiguousWordIdsAsync(connection, transaction, TopLevelWordIds(blocks), ct);
        var wanted = Spans(blocks, ambiguousWords).ToHashSet();
        var kept = new HashSet<AnalysisSpan>();
        var stale = new List<long>();
        command.CommandText = "SELECT id, block, s, e, word_id FROM analysis WHERE unit_id = $unit";
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var span = new AnalysisSpan(reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt64(4));
                if (wanted.Contains(span)) kept.Add(span);
                else stale.Add(reader.GetInt64(0));
            }
        }

        // Stale rows go first, because a new span can start where a deleted one did and (unit_id, block, s) is unique
        foreach (var batch in stale.Chunk(BatchSize))
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM analysis WHERE id IN ({AddInList(delete, "a", batch)})";
            await delete.ExecuteNonQueryAsync(ct);
        }

        await InsertPendingRowsAsync(connection, transaction, unitId, wanted.Where(span => !kept.Contains(span)),
            requestPriority.Value, ct);

        await transaction.CommitAsync(ct);
        return true;
    }

    /// <summary>
    /// Adds a pending row per span. A span whose (block, s) already has a row is skipped, so the row and its answer stay
    /// </summary>
    private async Task InsertPendingRowsAsync(SqliteConnection connection, SqliteTransaction transaction, long unitId,
        IEnumerable<AnalysisSpan> spans, int priority, CancellationToken ct)
    {
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO analysis (unit_id, block, s, e, word_id, state, priority, requested_utc)
            VALUES ($unit, $block, $s, $e, $word, 'pending', $priority, $now)
            ON CONFLICT (unit_id, block, s) DO NOTHING
            """;
        insert.Parameters.AddWithValue("$unit", unitId);
        insert.Parameters.AddWithValue("$priority", priority);
        insert.Parameters.AddWithValue("$now", Now());
        var block = insert.Parameters.Add("$block", SqliteType.Integer);
        var s = insert.Parameters.Add("$s", SqliteType.Integer);
        var e = insert.Parameters.Add("$e", SqliteType.Integer);
        var word = insert.Parameters.Add("$word", SqliteType.Integer);
        foreach (var span in spans)
        {
            block.Value = span.Block;
            s.Value = span.S;
            e.Value = span.E;
            word.Value = span.WordId;
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    private static IEnumerable<long> TopLevelWordIds(IEnumerable<StoredBlock> blocks) =>
        blocks.SelectMany(b => b.Tokens).Select(t => t.W).OfType<long>();

    /// <summary>
    /// The top-level tokens whose word is in <paramref name="ambiguousWords"/>. Parts of compounds never get a row
    /// </summary>
    private static IEnumerable<AnalysisSpan> Spans(IEnumerable<StoredBlock> blocks, HashSet<long> ambiguousWords) =>
        blocks.SelectMany(b => b.Tokens
            .Where(t => t.W is { } w && ambiguousWords.Contains(w))
            .Select(t => new AnalysisSpan(b.Index, t.S, t.E, t.W!.Value)));

    /// <returns>The ids among <paramref name="wordIds"/> whose stored word is ambiguous by <see cref="AmbiguousWord"/></returns>
    private static async Task<HashSet<long>> AmbiguousWordIdsAsync(SqliteConnection connection,
        SqliteTransaction transaction, IEnumerable<long> wordIds, CancellationToken ct)
    {
        var result = new HashSet<long>();
        foreach (var batch in wordIds.Distinct().Chunk(BatchSize))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT id FROM word WHERE id IN ({AddInList(command, "w", batch)}) AND {AmbiguousWord}";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) result.Add(reader.GetInt64(0));
        }
        return result;
    }

    /// <summary>
    /// Replaces the unit's reading_mark and word_use rows with the ones its top-level tokens give
    /// </summary>
    private static async Task ReplaceMarksAsync(SqliteConnection connection, SqliteTransaction transaction,
        long unitId, IReadOnlyList<StoredBlock> blocks, CancellationToken ct)
    {
        var lemmas = await LemmasAsync(connection, transaction,
            blocks.Where(b => b.Ruby.Count > 0).SelectMany(b => b.Tokens).Select(t => t.W).OfType<long>(), ct);
        var (marks, uses) = PageMarks.Count(blocks, lemmas);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM reading_mark WHERE unit_id = $unit; DELETE FROM word_use WHERE unit_id = $unit;";
        command.Parameters.AddWithValue("$unit", unitId);
        await command.ExecuteNonQueryAsync(ct);

        command.CommandText = "INSERT INTO word_use (unit_id, word_id, uses) VALUES ($unit, $word, $count)";
        var word = command.Parameters.Add("$word", SqliteType.Integer);
        var count = command.Parameters.Add("$count", SqliteType.Integer);
        foreach (var (wordId, n) in uses)
        {
            word.Value = wordId;
            count.Value = n;
            await command.ExecuteNonQueryAsync(ct);
        }

        command.CommandText = """
            INSERT INTO reading_mark (unit_id, word_id, reading, printed, count)
            VALUES ($unit, $word, $reading, $printed, $count)
            """;
        var reading = command.Parameters.Add("$reading", SqliteType.Text);
        var printed = command.Parameters.Add("$printed", SqliteType.Text);
        foreach (var mark in marks)
        {
            word.Value = mark.WordId;
            reading.Value = mark.Reading;
            printed.Value = mark.Printed;
            count.Value = mark.Count;
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<Dictionary<long, string>> LemmasAsync(SqliteConnection connection,
        SqliteTransaction transaction, IEnumerable<long> wordIds, CancellationToken ct)
    {
        var result = new Dictionary<long, string>();
        foreach (var batch in wordIds.Distinct().Chunk(BatchSize))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT id, lemma FROM word WHERE id IN ({AddInList(command, "w", batch)})";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) result[reader.GetInt64(0)] = reader.GetString(1);
        }
        return result;
    }

    public Task<IReadOnlyDictionary<long, WordMarkTotals>> GetReadingMarksAsync(int mangaFileId,
        IReadOnlyCollection<long> wordIds, CancellationToken ct = default) =>
        GetReadingMarksAsync([mangaFileId], wordIds, ct);

    public async Task<IReadOnlyDictionary<long, WordMarkTotals>> GetReadingMarksAsync(
        IReadOnlyCollection<int> mangaFileIds, IReadOnlyCollection<long> wordIds, CancellationToken ct = default)
    {
        if (mangaFileIds.Count == 0 || wordIds.Count == 0) return new Dictionary<long, WordMarkTotals>();

        var uses = new Dictionary<long, int>();
        var marks = new Dictionary<(long WordId, string Reading, string Printed), int>();
        await using var connection = await database.OpenConnectionAsync(ct);
        // One snapshot for every batch, so a page saved meanwhile counts in both tables or in neither
        await using var snapshot = connection.BeginTransaction(deferred: true);
        foreach (var files in mangaFileIds.Distinct().Chunk(MarkBatchSize))
        foreach (var words in wordIds.Distinct().Chunk(MarkBatchSize))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = snapshot;
            command.Parameters.AddWithValue("$kind", Epub);
            var fileList = AddInList(command, "f", files);
            var wordList = AddInList(command, "w", words);
            command.CommandText = ReadingMarkTotalsSql(fileList, wordList) + ";\n" + WordUseTotalsSql(fileList, wordList);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var key = (reader.GetInt64(0), reader.GetString(1), reader.GetString(2));
                marks[key] = marks.GetValueOrDefault(key) + reader.GetInt32(3);
            }
            await reader.NextResultAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var wordId = reader.GetInt64(0);
                uses[wordId] = uses.GetValueOrDefault(wordId) + reader.GetInt32(1);
            }
        }

        // A reading's printed form is the one counted most over every page asked about
        var readings = marks
            .GroupBy(m => (m.Key.WordId, m.Key.Reading))
            .Select(g => (g.Key.WordId, Total: new ReadingMarkTotal(g.Key.Reading,
                g.OrderByDescending(m => m.Value).ThenBy(m => m.Key.Printed, StringComparer.Ordinal).First().Key.Printed,
                g.Sum(m => m.Value))))
            .ToLookup(r => r.WordId, r => r.Total);

        return uses.Keys.Concat(readings.Select(r => r.Key)).Distinct().ToDictionary(id => id, id =>
            new WordMarkTotals(id, uses.GetValueOrDefault(id), readings[id]
                .OrderByDescending(r => r.Count).ThenBy(r => r.Reading, StringComparer.Ordinal).ToList()));
    }

    public async Task<ReadingUnitState> RecordFailedAttemptAsync(long unitId, int maxAttempts, CancellationToken ct = default)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var transaction = BeginWrite(connection);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$unit", unitId);

        command.CommandText = "SELECT state, attempts FROM unit WHERE id = $unit";
        ReadingUnitState state;
        int attempts;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            // A unit deleted while it was being tokenized reports failed, so the worker drops it and doesn't retry
            if (!await reader.ReadAsync(ct)) return ReadingUnitState.Failed;
            state = ParseUnitState(reader.GetString(0));
            attempts = reader.GetInt32(1);
        }
        if (state is not (ReadingUnitState.Pending or ReadingUnitState.Refresh)) return state;

        // A refresh that keeps failing leaves the unit with the blocks it had, which still match the file
        attempts++;
        if (attempts >= maxAttempts)
        {
            state = state == ReadingUnitState.Refresh ? ReadingUnitState.Done : ReadingUnitState.Failed;
            if (state == ReadingUnitState.Done) attempts = 0;
        }
        command.CommandText = """
            UPDATE unit SET attempts = $attempts, state = $state, requested_utc = $now WHERE id = $unit
            """;
        command.Parameters.AddWithValue("$attempts", attempts);
        command.Parameters.AddWithValue("$state", Name(state));
        command.Parameters.AddWithValue("$now", Now());
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return state;
    }

    public async Task DeferUnitAsync(long unitId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE unit SET requested_utc = $now WHERE id = $unit AND state IN ('pending', 'refresh')";
        command.Parameters.AddWithValue("$unit", unitId);
        command.Parameters.AddWithValue("$now", Now());
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<ReadingUnit?> GetUnitAsync(int mangaFileId, int page, CancellationToken ct = default)
    {
        SqliteConnection connection;
        try
        {
            connection = await database.OpenConnectionAsync(ct);
        }
        catch (ReadingStoreNotReadyException)
        {
            return null;
        }

        await using var _ = connection;
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, kind, state, source_stamp, tokenizer_version, blocks
            FROM unit
            WHERE kind = $kind AND manga_file_id = $file AND page = $page
            """;
        command.Parameters.AddWithValue("$kind", Epub);
        command.Parameters.AddWithValue("$file", mangaFileId);
        command.Parameters.AddWithValue("$page", page);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new ReadingUnit(reader.GetInt64(0), ParseKind(reader.GetString(1)), mangaFileId, page,
            ParseUnitState(reader.GetString(2)), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? [] : ReadingBlocks.Decode(reader.GetValue(5), reader.GetInt64(0)));
    }

    public async Task RaiseUnitPriorityAsync(int mangaFileId, int? page, int priority, CancellationToken ct = default)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = RaiseUnitPrioritySql;
        command.Parameters.AddWithValue("$kind", Epub);
        command.Parameters.AddWithValue("$file", mangaFileId);
        command.Parameters.AddWithValue("$page", (object?) page ?? DBNull.Value);
        command.Parameters.AddWithValue("$priority", priority);
        command.Parameters.AddWithValue("$now", Now());
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<StoredAnalysis>> GetUnitAnalysisAsync(long unitId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, block, s, e, word_id, state, priority, sense_key, reading, generator, prompt_version
            FROM analysis
            WHERE unit_id = $unit
            ORDER BY block, s
            """;
        command.Parameters.AddWithValue("$unit", unitId);

        var result = new List<StoredAnalysis>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new StoredAnalysis(reader.GetInt64(0), unitId, reader.GetInt32(1), reader.GetInt32(2),
                reader.GetInt32(3), reader.GetInt64(4), ParseAnalysisState(reader.GetString(5)), reader.GetInt32(6),
                NullableString(reader, 7), NullableString(reader, 8), NullableString(reader, 9),
                NullableString(reader, 10)));
        }
        return result;
    }

    public async Task RequestAnalysisAsync(IReadOnlyCollection<int> mangaFileIds, int priority,
        CancellationToken ct = default)
    {
        if (mangaFileIds.Count == 0) return;

        await using var connection = await database.OpenConnectionAsync(ct);
        foreach (var file in mangaFileIds.Distinct())
        {
            await using var transaction = BeginWrite(connection);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$kind", Epub);
            command.Parameters.AddWithValue("$file", file);
            command.Parameters.AddWithValue("$page", DBNull.Value);
            command.Parameters.AddWithValue("$block", DBNull.Value);
            command.Parameters.AddWithValue("$priority", priority);
            command.Parameters.AddWithValue("$now", Now());

            command.CommandText = """
                INSERT INTO analysis_request (manga_file_id, priority, requested_utc) VALUES ($file, $priority, $now)
                ON CONFLICT (manga_file_id) DO UPDATE SET priority = MAX(priority, excluded.priority)
                RETURNING priority
                """;
            var requestPriority = Convert.ToInt32(await command.ExecuteScalarAsync(ct));

            command.CommandText = RaiseAnalysisPrioritySql;
            await command.ExecuteNonQueryAsync(ct);

            await AddMissingRowsAsync(connection, transaction, file, requestPriority, ct);
            await transaction.CommitAsync(ct);
        }
    }

    /// <summary>
    /// Gives each unit of the file that has blocks and no rows its pending rows. Every write of rows covers a whole
    /// unit in one transaction, so a unit with any row already has all of them
    /// </summary>
    private async Task AddMissingRowsAsync(SqliteConnection connection, SqliteTransaction transaction, int file,
        int priority, CancellationToken ct)
    {
        var afterPage = -1;
        while (true)
        {
            var units = new List<(long Id, List<StoredBlock> Blocks)>();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    SELECT u.id, u.page, u.blocks FROM unit u
                    WHERE u.kind = $kind AND u.manga_file_id = $file AND u.page > $after AND u.blocks IS NOT NULL
                      AND NOT EXISTS (SELECT 1 FROM analysis a WHERE a.unit_id = u.id)
                    ORDER BY u.page
                    LIMIT $limit
                    """;
                command.Parameters.AddWithValue("$kind", Epub);
                command.Parameters.AddWithValue("$file", file);
                command.Parameters.AddWithValue("$after", afterPage);
                command.Parameters.AddWithValue("$limit", UnitsPerRequestBatch);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    units.Add((reader.GetInt64(0), ReadingBlocks.Decode(reader.GetValue(2), reader.GetInt64(0))));
                    afterPage = reader.GetInt32(1);
                }
            }
            if (units.Count == 0) return;

            var ambiguousWords = await AmbiguousWordIdsAsync(connection, transaction,
                units.SelectMany(u => TopLevelWordIds(u.Blocks)), ct);
            foreach (var (unitId, blocks) in units)
            {
                await InsertPendingRowsAsync(connection, transaction, unitId, Spans(blocks, ambiguousWords), priority, ct);
            }
        }
    }

    public async Task RaiseAnalysisPriorityAsync(int mangaFileId, int? page, int? block, int priority,
        CancellationToken ct = default)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = RaiseAnalysisPrioritySql;
        command.Parameters.AddWithValue("$kind", Epub);
        command.Parameters.AddWithValue("$file", mangaFileId);
        command.Parameters.AddWithValue("$page", (object?) page ?? DBNull.Value);
        command.Parameters.AddWithValue("$block", (object?) block ?? DBNull.Value);
        command.Parameters.AddWithValue("$priority", priority);
        command.Parameters.AddWithValue("$now", Now());
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<PendingAnalysisRow>> GetPendingAnalysisAsync(int limit, int minPriority,
        CancellationToken ct = default)
    {
        if (limit <= 0) return [];

        await using var connection = await database.OpenConnectionAsync(ct);
        // One deferred transaction reads one WAL snapshot, so a re-tokenize that commits between the two reads can't
        // pair a row's offsets with another version of its block's text
        await using var snapshot = connection.BeginTransaction(deferred: true);
        var rows = new List<(long Id, long UnitId, int File, int Page, int Block, int S, int E, long Word, int Priority)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = snapshot;
            command.CommandText = PendingAnalysisSql;
            command.Parameters.AddWithValue("$minPriority", minPriority);
            command.Parameters.AddWithValue("$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetInt32(3),
                    reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt64(7), reader.GetInt32(8)));
            }
        }
        if (rows.Count == 0) return [];

        var texts = new Dictionary<(long Unit, int Block), string>();
        foreach (var batch in rows.Select(r => r.UnitId).Distinct().Chunk(BatchSize))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = snapshot;
            command.CommandText = $"SELECT id, blocks FROM unit WHERE id IN ({AddInList(command, "u", batch)}) AND blocks IS NOT NULL";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var unitId = reader.GetInt64(0);
                foreach (var stored in ReadingBlocks.Decode(reader.GetValue(1), unitId))
                {
                    texts[(unitId, stored.Index)] = stored.Text;
                }
            }
        }

        return rows
            .Select(r => new PendingAnalysisRow(r.Id, r.UnitId, r.File, r.Page, r.Block, r.S, r.E, r.Word, r.Priority,
                texts.GetValueOrDefault((r.UnitId, r.Block), string.Empty)))
            .ToList();
    }

    public async Task<AnalysisStoreOutcome> StoreAnalysisAsync(string generator, string promptVersion,
        IReadOnlyList<AnalysisAnswer> answers, CancellationToken ct = default)
    {
        if (answers.Count == 0) return new AnalysisStoreOutcome(0, []);

        await using var connection = await database.OpenConnectionAsync(ct);
        await using var transaction = BeginWrite(connection);

        var rows = new Dictionary<long, AnswerTarget>();
        foreach (var batch in answers.Select(a => a.Id).Distinct().Chunk(BatchSize))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT a.id, a.state, a.word_id, u.manga_file_id, u.page
                FROM analysis a
                JOIN unit u ON u.id = a.unit_id
                WHERE a.id IN ({AddInList(command, "a", batch)})
                """;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows[reader.GetInt64(0)] = new AnswerTarget(ParseAnalysisState(reader.GetString(1)), reader.GetInt64(2),
                    new AnalysisStoredPage(reader.GetInt32(3), reader.GetInt32(4)));
            }
        }

        var words = new Dictionary<long, StoredWord>();
        await ReadWordsAsync(connection, transaction, rows.Values.Select(r => r.WordId).Distinct().ToList(), words, ct);

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE analysis
            SET state = 'done', sense_key = $sense, reading = $reading, generator = $generator, prompt_version = $prompt
            WHERE id = $id
            """;
        update.Parameters.AddWithValue("$generator", generator);
        update.Parameters.AddWithValue("$prompt", promptVersion);
        var id = update.Parameters.Add("$id", SqliteType.Integer);
        var sense = update.Parameters.Add("$sense", SqliteType.Text);
        var reading = update.Parameters.Add("$reading", SqliteType.Text);

        var accepted = new HashSet<long>();
        var pages = new HashSet<AnalysisStoredPage>();
        var rejected = new List<AnalysisRejection>();
        foreach (var answer in answers)
        {
            var reason = Check(answer, accepted, rows, words);
            if (reason != null)
            {
                rejected.Add(new AnalysisRejection(answer.Id, reason.Value));
                continue;
            }

            id.Value = answer.Id;
            sense.Value = (object?) answer.SenseKey ?? DBNull.Value;
            reading.Value = (object?) answer.Reading ?? DBNull.Value;
            await update.ExecuteNonQueryAsync(ct);
            accepted.Add(answer.Id);
            pages.Add(rows[answer.Id].Page);
        }

        await transaction.CommitAsync(ct);
        return new AnalysisStoreOutcome(accepted.Count, rejected)
        {
            StoredPages = pages.OrderBy(p => p.MangaFileId).ThenBy(p => p.Page).ToList(),
        };
    }

    public async Task ClearAnalysisAsync(IReadOnlyCollection<int> mangaFileIds, CancellationToken ct = default)
    {
        if (mangaFileIds.Count == 0) return;

        await using var connection = await database.OpenConnectionAsync(ct);
        await using var transaction = BeginWrite(connection);
        foreach (var batch in mangaFileIds.Distinct().Chunk(BatchSize))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$kind", Epub);
            var files = AddInList(command, "f", batch);
            command.CommandText = $"""
                DELETE FROM analysis_request WHERE manga_file_id IN ({files});
                DELETE FROM analysis WHERE unit_id IN (
                    SELECT id FROM unit WHERE kind = $kind AND manga_file_id IN ({files}));
                """;
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    public async Task<ReadingStoreCounts> GetCountsAsync(IReadOnlyCollection<int>? mangaFileIds,
        CancellationToken ct = default)
    {
        var units = new Dictionary<string, int>();
        var analysis = new Dictionary<string, int>();

        await using var connection = await database.OpenConnectionAsync(ct);
        IEnumerable<int[]?> batches = mangaFileIds == null ? [null] : mangaFileIds.Distinct().Chunk(BatchSize);
        foreach (var batch in batches)
        {
            await using var command = connection.CreateCommand();
            command.Parameters.AddWithValue("$kind", Epub);
            var filter = batch == null ? string.Empty : $"AND u.manga_file_id IN ({AddInList(command, "f", batch)})";
            command.CommandText = $"""
                SELECT 'unit', u.state, COUNT(*) FROM unit u WHERE u.kind = $kind {filter} GROUP BY u.state
                UNION ALL
                SELECT 'analysis', a.state, COUNT(*) FROM analysis a
                JOIN unit u ON u.id = a.unit_id
                WHERE u.kind = $kind {filter}
                GROUP BY a.state
                """;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var target = reader.GetString(0) == "unit" ? units : analysis;
                var state = reader.GetString(1);
                target[state] = target.GetValueOrDefault(state) + reader.GetInt32(2);
            }
        }

        return new ReadingStoreCounts(
            units.GetValueOrDefault(Name(ReadingUnitState.Pending)) + units.GetValueOrDefault(Name(ReadingUnitState.Refresh)),
            units.GetValueOrDefault(Name(ReadingUnitState.Done)),
            units.GetValueOrDefault(Name(ReadingUnitState.Failed)),
            analysis.GetValueOrDefault(Name(AnalysisState.Pending)),
            analysis.GetValueOrDefault(Name(AnalysisState.Done)));
    }

    /// <param name="accepted">Ids already stored in this batch. A rejected answer doesn't count, so a later answer
    /// for the same id still gets its own check</param>
    private static AnalysisRejectReason? Check(AnalysisAnswer answer, HashSet<long> accepted,
        Dictionary<long, AnswerTarget> rows, Dictionary<long, StoredWord> words)
    {
        if (accepted.Contains(answer.Id)) return AnalysisRejectReason.Duplicate;
        if (!rows.TryGetValue(answer.Id, out var row) || row.State != AnalysisState.Pending)
        {
            return AnalysisRejectReason.NotPending;
        }

        var word = words.GetValueOrDefault(row.WordId);
        if (answer.SenseKey != null && (word == null || word.Senses.All(s => s.Key != answer.SenseKey)))
        {
            return AnalysisRejectReason.SenseNotOffered;
        }
        if (answer.Reading != null &&
            (word == null || (word.Reading != answer.Reading && !word.Readings.Contains(answer.Reading))))
        {
            return AnalysisRejectReason.ReadingNotOffered;
        }
        return null;
    }

    private static async Task ReadWordsAsync(SqliteConnection connection, SqliteTransaction? transaction,
        IReadOnlyCollection<long> ids, Dictionary<long, StoredWord> result, CancellationToken ct)
    {
        foreach (var batch in ids.Distinct().Chunk(BatchSize))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT id, lemma, pos, is_proper, reading, jlpt, readings, senses, lexicon_version
                FROM word WHERE id IN ({AddInList(command, "w", batch)})
                """;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetInt64(0);
                result[id] = new StoredWord(id, reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0,
                    reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    JsonSerializer.Deserialize<List<string>>(reader.GetString(6)) ?? [],
                    JsonSerializer.Deserialize<List<SenseDto>>(reader.GetString(7)) ?? [],
                    reader.GetString(8));
            }
        }
    }

    /// <summary>
    /// Adds one parameter per value and returns their names joined for an IN list
    /// </summary>
    private static string AddInList<T>(SqliteCommand command, string prefix, IReadOnlyList<T> values)
    {
        var names = new string[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            names[i] = $"${prefix}{i}";
            command.Parameters.AddWithValue(names[i], values[i]);
        }
        return string.Join(", ", names);
    }

    /// <summary>
    /// Microsoft.Data.Sqlite opens a transaction with BEGIN IMMEDIATE unless it's asked for a deferred one
    /// </summary>
    private static SqliteTransaction BeginWrite(SqliteConnection connection) => connection.BeginTransaction(deferred: false);

    private long Now() => _time.GetUtcNow().UtcTicks;

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string Name<TEnum>(TEnum value) where TEnum : struct, Enum => value.ToString().ToLowerInvariant();

    private static ReadingUnitKind ParseKind(string value) => Enum.Parse<ReadingUnitKind>(value, ignoreCase: true);

    private static ReadingUnitState ParseUnitState(string value) => Enum.Parse<ReadingUnitState>(value, ignoreCase: true);

    private static AnalysisState ParseAnalysisState(string value) => Enum.Parse<AnalysisState>(value, ignoreCase: true);

    private sealed record AnswerTarget(AnalysisState State, long WordId, AnalysisStoredPage Page);

    /// <summary>
    /// Where an analysis row sits, found by its top-level token
    /// </summary>
    private sealed record AnalysisSpan(int Block, int S, int E, long WordId);
}
