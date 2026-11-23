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
    /// The next pending unit in queue order. Public so the tests can check that it reads ix_unit_queue
    /// </summary>
    public const string NextPendingUnitSql = """
        SELECT id, kind, manga_file_id, page, source_stamp, attempts
        FROM unit
        WHERE state = 'pending'
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
          AND +state = 'pending' AND +priority < $priority
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
    /// Ids per IN list. Older SQLite builds cap a statement at 999 parameters
    /// </summary>
    private const int BatchSize = 500;

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
            readings.Value = JsonSerializer.Serialize(word.Readings);
            senses.Value = JsonSerializer.Serialize(word.Senses);
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
        await using var command = connection.CreateCommand();
        command.CommandText = NextPendingUnitSql;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new ReadingUnitRef(reader.GetInt64(0), ParseKind(reader.GetString(1)), reader.GetInt32(2),
            reader.GetInt32(3), reader.GetString(4), reader.GetInt32(5));
    }

    public async Task<bool> SaveTokenizedUnitAsync(long unitId, string sourceStamp, string tokenizerVersion,
        IReadOnlyList<StoredBlock> blocks, IReadOnlyList<AnalysisSpan> ambiguous, CancellationToken ct = default)
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
        command.Parameters.AddWithValue("$blocks", SerializeBlocks(blocks));
        await command.ExecuteNonQueryAsync(ct);

        command.CommandText = "SELECT priority FROM analysis_request WHERE manga_file_id = $file";
        command.Parameters.AddWithValue("$file", mangaFileId);
        var requestPriority = await command.ExecuteScalarAsync(ct) is { } p ? Convert.ToInt32(p) : (int?) null;

        var wanted = ambiguous.ToHashSet();
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

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO analysis (unit_id, block, s, e, word_id, state, priority, requested_utc)
            VALUES ($unit, $block, $s, $e, $word, $state, $priority, $now)
            ON CONFLICT (unit_id, block, s) DO NOTHING
            """;
        insert.Parameters.AddWithValue("$unit", unitId);
        insert.Parameters.AddWithValue("$state", requestPriority == null ? "idle" : "pending");
        insert.Parameters.AddWithValue("$priority", requestPriority ?? ReadingPriority.WholeBook);
        insert.Parameters.AddWithValue("$now", Now());
        var block = insert.Parameters.Add("$block", SqliteType.Integer);
        var s = insert.Parameters.Add("$s", SqliteType.Integer);
        var e = insert.Parameters.Add("$e", SqliteType.Integer);
        var word = insert.Parameters.Add("$word", SqliteType.Integer);
        foreach (var span in wanted.Where(span => !kept.Contains(span)))
        {
            block.Value = span.Block;
            s.Value = span.S;
            e.Value = span.E;
            word.Value = span.WordId;
            await insert.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return true;
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
        if (state != ReadingUnitState.Pending) return state;

        attempts++;
        state = attempts >= maxAttempts ? ReadingUnitState.Failed : ReadingUnitState.Pending;
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

    public async Task<ReadingUnit?> GetUnitAsync(int mangaFileId, int page, CancellationToken ct = default)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
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
            reader.IsDBNull(5) ? [] : DeserializeBlocks(reader.GetString(5)));
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
        await using var transaction = BeginWrite(connection);
        foreach (var batch in mangaFileIds.Distinct().Chunk(BatchSize))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$kind", Epub);
            command.Parameters.AddWithValue("$priority", priority);
            command.Parameters.AddWithValue("$now", Now());
            AddInList(command, "f", batch);
            var values = string.Join(", ", batch.Select((_, i) => $"($f{i}, $priority, $now)"));
            command.CommandText = $"""
                INSERT INTO analysis_request (manga_file_id, priority, requested_utc) VALUES {values}
                ON CONFLICT (manga_file_id) DO UPDATE SET priority = MAX(priority, excluded.priority);
                {QueueRequestedAnalysisSql(batch.Length)};
                {RaiseRequestedAnalysisSql(batch.Length)};
                """;
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// Turns the idle analysis rows of the files <c>$f0</c> to <c>$f{fileCount - 1}</c> pending. The unary plus keeps
    /// the planner off ix_analysis_queue, whose idle range is every book nobody asked for. Public so the tests can
    /// check the plan
    /// </summary>
    public static string QueueRequestedAnalysisSql(int fileCount) => $"""
        UPDATE analysis SET state = 'pending', priority = $priority, requested_utc = $now
        WHERE +state = 'idle'
          AND unit_id IN (SELECT id FROM unit WHERE kind = $kind AND manga_file_id IN ({FileList(fileCount)}))
        """;

    /// <summary>
    /// Raises the pending analysis rows of the files <c>$f0</c> to <c>$f{fileCount - 1}</c>, found by unit_id as in
    /// <see cref="QueueRequestedAnalysisSql"/>. Public so the tests can check the plan
    /// </summary>
    public static string RaiseRequestedAnalysisSql(int fileCount) => $"""
        UPDATE analysis SET priority = $priority, requested_utc = $now
        WHERE +state = 'pending' AND +priority < $priority
          AND unit_id IN (SELECT id FROM unit WHERE kind = $kind AND manga_file_id IN ({FileList(fileCount)}))
        """;

    private static string FileList(int count) => string.Join(", ", Enumerable.Range(0, count).Select(i => $"$f{i}"));

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
                foreach (var stored in DeserializeBlocks(reader.GetString(1)))
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
            command.Parameters.AddWithValue("$priority", ReadingPriority.WholeBook);
            var files = AddInList(command, "f", batch);
            command.CommandText = $"""
                DELETE FROM analysis_request WHERE manga_file_id IN ({files});

                UPDATE analysis
                SET state = 'idle', priority = $priority, sense_key = NULL, reading = NULL, generator = NULL,
                    prompt_version = NULL
                WHERE unit_id IN (SELECT id FROM unit WHERE kind = $kind AND manga_file_id IN ({files}));
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
            units.GetValueOrDefault(Name(ReadingUnitState.Pending)),
            units.GetValueOrDefault(Name(ReadingUnitState.Done)),
            units.GetValueOrDefault(Name(ReadingUnitState.Failed)),
            analysis.GetValueOrDefault(Name(AnalysisState.Idle)),
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

    private static string SerializeBlocks(IReadOnlyList<StoredBlock> blocks) =>
        JsonSerializer.Serialize(blocks.Select(b => new BlockJson(b.Index, b.Text, b.Tokens)).ToList());

    private static List<StoredBlock> DeserializeBlocks(string json) =>
        (JsonSerializer.Deserialize<List<BlockJson>>(json) ?? [])
        .Select(b => new StoredBlock(b.Index, b.Text, b.Tokens))
        .ToList();

    private sealed record AnswerTarget(AnalysisState State, long WordId, AnalysisStoredPage Page);

    /// <summary>
    /// A block in unit.blocks. Tokens use TokenDto's array converters, the same form the sidecar and the reader use
    /// </summary>
    private sealed record BlockJson(
        [property: JsonPropertyName("i")] int Index,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("tokens")] IReadOnlyList<TokenDto> Tokens);
}
