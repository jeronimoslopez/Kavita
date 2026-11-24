using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kavita.API.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kavita.Services.Dictionary;

/// <summary>
/// Owns config/reading/reading.db, the SQLite file behind <see cref="ReadingStore"/>. It's kept out of kavita.db so
/// tokens and analysis stay out of Kavita's backups, and the tokenizer never waits behind kavita.db's writer
/// </summary>
/// <remarks>
/// The schema version is <c>PRAGMA user_version</c>. Step n of <see cref="Migrations"/> takes the file from version n
/// to n + 1, so a schema change is one appended step and never an edit to an old one. Each step sets the version
/// itself, as its last write
/// </remarks>
public class ReadingDatabase
{
    /// <summary>
    /// Unit and analysis ids are AUTOINCREMENT so a deleted id never comes back. The worker and the runner hold ids
    /// across calls, and a reused id would let an answer or a tokenize land on a different row. Queue times are UTC
    /// ticks. Each queue index runs (state, priority DESC, requested_utc, id), the order
    /// IReadingStore documents, so taking the next row reads the front of the index
    /// </summary>
    internal const string Version1Schema = """
        CREATE TABLE word (
            id INTEGER PRIMARY KEY,
            lemma TEXT NOT NULL,
            pos TEXT NOT NULL,
            is_proper INTEGER NOT NULL,
            reading TEXT NOT NULL,
            jlpt INTEGER NULL,
            readings TEXT NOT NULL,
            senses TEXT NOT NULL,
            lexicon_version TEXT NOT NULL,
            UNIQUE (lemma, pos, is_proper)
        );

        CREATE TABLE unit (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            kind TEXT NOT NULL,
            manga_file_id INTEGER NOT NULL,
            page INTEGER NOT NULL,
            source_stamp TEXT NOT NULL,
            tokenizer_version TEXT NULL,
            state TEXT NOT NULL,
            attempts INTEGER NOT NULL DEFAULT 0,
            priority INTEGER NOT NULL,
            requested_utc INTEGER NOT NULL,
            blocks TEXT NULL,
            UNIQUE (kind, manga_file_id, page)
        );
        CREATE INDEX ix_unit_queue ON unit (state, priority DESC, requested_utc, id);

        CREATE TABLE analysis (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            unit_id INTEGER NOT NULL,
            block INTEGER NOT NULL,
            s INTEGER NOT NULL,
            e INTEGER NOT NULL,
            word_id INTEGER NOT NULL,
            state TEXT NOT NULL,
            priority INTEGER NOT NULL,
            requested_utc INTEGER NOT NULL,
            sense_key TEXT NULL,
            reading TEXT NULL,
            generator TEXT NULL,
            prompt_version TEXT NULL,
            UNIQUE (unit_id, block, s)
        );
        CREATE INDEX ix_analysis_queue ON analysis (state, priority DESC, requested_utc, id);

        CREATE TABLE analysis_request (
            manga_file_id INTEGER PRIMARY KEY,
            priority INTEGER NOT NULL,
            requested_utc INTEGER NOT NULL
        );
        """;

    private static readonly IReadOnlyList<Func<ReadingDatabase, SqliteConnection, CancellationToken, Task>> Migrations =
    [
        (_, connection, ct) => CreateTablesAsync(connection, ct),
        (database, connection, ct) => database.ShrinkAsync(connection, ct),
    ];

    private readonly string _connectionString;
    private readonly ILogger _logger;
    private readonly Lock _initGate = new();
    private Task? _init;
    /// <summary>
    /// Completes once the running initialisation knows whether it has existing rows to rewrite
    /// </summary>
    private TaskCompletionSource _initDecided = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _initialized;
    private volatile bool _rewriting;

    public ReadingDatabase(string databasePath, ILogger<ReadingDatabase>? logger = null)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        _logger = logger ?? NullLogger<ReadingDatabase>.Instance;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
    }

    /// <summary>
    /// config/reading/reading.db, resolved against the working directory the same way DirectoryService resolves config/
    /// </summary>
    public static string DefaultDatabasePath =>
        Path.Join(Directory.GetCurrentDirectory(), "config", "reading", "reading.db");

    /// <summary>
    /// The <c>user_version</c> a file has once every migration has run
    /// </summary>
    public static int SchemaVersion => Migrations.Count;

    public string DatabasePath { get; }

    /// <summary>
    /// Units per transaction in the version 2 rewrite. Words go ten times as many per transaction, being far smaller.
    /// Settable so tests can run the rewrite in several batches
    /// </summary>
    internal int MigrationUnitsPerBatch { get; init; } = 100;

    /// <summary>
    /// Called with the phase name after each committed batch of the version 2 rewrite, so tests can stop it midway
    /// </summary>
    internal Action<string>? AfterMigrationBatch { get; init; }

    /// <summary>
    /// Creates the file and brings its schema up to date, unless that's done already. The tokenize worker calls this
    /// at startup, so an upgrade that rewrites existing rows runs there and not inside a web request
    /// </summary>
    public Task InitializeAsync(CancellationToken ct = default) =>
        _initialized ? Task.CompletedTask : StartInitialization().Init.WaitAsync(ct);

    /// <summary>
    /// Opens a pooled connection, creating the file and bringing its schema up to date on first use
    /// </summary>
    /// <exception cref="ReadingStoreNotReadyException">An upgrade is rewriting the file's existing rows</exception>
    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct = default)
    {
        if (!_initialized)
        {
            var (init, decided) = StartInitialization();
            await Task.WhenAny(init, decided).WaitAsync(ct);
            if (!init.IsCompleted && _rewriting) throw new ReadingStoreNotReadyException();
            await init.WaitAsync(ct);
        }

        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            // Several services and the tokenize worker write at once, and the busy timeout makes a second writer
            // wait for the first instead of failing with SQLITE_BUSY
            command.CommandText = "PRAGMA busy_timeout = 10000; PRAGMA synchronous = NORMAL; PRAGMA temp_store = MEMORY;";
            await command.ExecuteNonQueryAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <returns>The running initialisation, or a new one when none has run or the last one failed, with the task that
    /// completes once it knows whether it rewrites rows</returns>
    private (Task Init, Task Decided) StartInitialization()
    {
        lock (_initGate)
        {
            if (_init is not { IsFaulted: false, IsCanceled: false })
            {
                _initDecided = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _init = Task.Run(InitializeCoreAsync);
            }
            return (_init, _initDecided.Task);
        }
    }

    /// <summary>
    /// Runs on its own task with no caller's token, because every caller shares it and a caller that gives up mustn't
    /// stop it for the rest
    /// </summary>
    private async Task InitializeCoreAsync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 10000; PRAGMA synchronous = NORMAL;";
                await command.ExecuteNonQueryAsync();
            }

            await MigrateAsync(connection, CancellationToken.None);
            _initialized = true;
        }
        finally
        {
            _rewriting = false;
            _initDecided.TrySetResult();
        }
    }

    private async Task MigrateAsync(SqliteConnection connection, CancellationToken ct)
    {
        var version = await UserVersionAsync(connection, null, ct);
        if (version > Migrations.Count)
        {
            throw new InvalidOperationException(
                $"reading.db is at schema version {version}, and this build only knows up to {Migrations.Count}");
        }

        // A file at version 1 or later holds tokenized books, and upgrading or compacting it can take minutes. Callers
        // other than the one that started this are turned away meanwhile instead of waiting
        var rewrites = version >= 1 && version < Migrations.Count;
        _rewriting = rewrites || await NeedsVacuumAsync(connection, ct);
        _initDecided.TrySetResult();

        for (var step = version; step < Migrations.Count; step++)
        {
            await Migrations[step](this, connection, ct);
        }

        // Decided from the file and not from the upgrade, so a compaction cut short by a restart or a full disk runs
        // again on a later start
        if (await NeedsVacuumAsync(connection, ct)) await VacuumAsync(connection, ct);
    }

    /// <summary>
    /// Files smaller than this aren't compacted, whatever their free pages, since the space isn't worth the rewrite.
    /// Settable so tests can compact small files
    /// </summary>
    internal long VacuumFloorBytes { get; init; } = 50_000_000;

    /// <returns>True when more than a quarter of the file's pages are free and the file is over
    /// <see cref="VacuumFloorBytes"/></returns>
    private async Task<bool> NeedsVacuumAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT page_count, freelist_count, page_size FROM pragma_page_count, pragma_freelist_count, pragma_page_size";
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return false;
        var pages = reader.GetInt64(0);
        var free = reader.GetInt64(1);
        return pages * reader.GetInt64(2) > VacuumFloorBytes && free * 4 > pages;
    }

    private static async Task<int> UserVersionAsync(SqliteConnection connection, SqliteTransaction? transaction,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    private static async Task CreateTablesAsync(SqliteConnection connection, CancellationToken ct)
    {
        // The version is read again inside the write transaction, so a second process opening the same file can't
        // run the step twice
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (await UserVersionAsync(connection, transaction, ct) != 0) return;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Version1Schema + "\nPRAGMA user_version = 1;";
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// Version 2. Deletes the analysis rows of files that have no analysis request, and any idle row. Then it stores
    /// each unit's blocks in the compressed form <see cref="ReadingBlocks"/> writes, and writes word.readings and
    /// word.senses again as JSON text without \uXXXX escapes. Words stay text because the ambiguity rule reads them
    /// in SQL
    /// </summary>
    /// <remarks>
    /// Every batch commits on its own and records where it got to in migration_progress, so the WAL stays small and a
    /// restart picks up after the last committed batch. Each batch can run twice without harm. The version moves to 2
    /// in the same transaction that drops migration_progress, after the last batch
    /// </remarks>
    private async Task ShrinkAsync(SqliteConnection connection, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS migration_progress (phase TEXT PRIMARY KEY, last_id INTEGER NOT NULL);
                SELECT COUNT(*) FROM unit;
                """;
            var units = Convert.ToInt64(await command.ExecuteScalarAsync(ct));
            if (units > 0)
            {
                _logger.LogInformation("[ReadingDatabase] Upgrading {Path} to schema 2 for {Units} pages. This " +
                                       "removes analysis rows of books nobody asked to analyze and compresses " +
                                       "stored text", DatabasePath, units);
            }
        }

        await RunPhaseAsync(connection, "analysis", "unit", MigrationUnitsPerBatch, DeleteUnrequestedAnalysisAsync, ct);
        await RunPhaseAsync(connection, "unit", "unit", MigrationUnitsPerBatch, RewriteBlocksAsync, ct);
        await RunPhaseAsync(connection, "word", "word", MigrationUnitsPerBatch * 10, RewriteWordsAsync, ct);

        await using (var transaction = connection.BeginTransaction(deferred: false))
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DROP TABLE migration_progress; PRAGMA user_version = 2;";
            await command.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
        }
        _logger.LogInformation("[ReadingDatabase] Schema 2 rows done in {Seconds:0.0}s", watch.Elapsed.TotalSeconds);
    }

    private delegate Task BatchAsync(SqliteConnection connection, SqliteTransaction transaction, List<long> ids,
        CancellationToken ct);

    /// <summary>
    /// Walks <paramref name="table"/> by id, one transaction per batch, from where migration_progress says the phase
    /// stopped
    /// </summary>
    private async Task RunPhaseAsync(SqliteConnection connection, string phase, string table, int batchSize,
        BatchAsync batch, CancellationToken ct)
    {
        long afterId;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT last_id FROM migration_progress WHERE phase = $phase";
            command.Parameters.AddWithValue("$phase", phase);
            afterId = await command.ExecuteScalarAsync(ct) is { } stored ? Convert.ToInt64(stored) : 0;
        }

        var batches = 0;
        while (true)
        {
            await using var transaction = connection.BeginTransaction(deferred: false);
            var ids = new List<long>(batchSize);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"SELECT id FROM {table} WHERE id > $after ORDER BY id LIMIT $limit";
                command.Parameters.AddWithValue("$after", afterId);
                command.Parameters.AddWithValue("$limit", batchSize);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) ids.Add(reader.GetInt64(0));
            }
            if (ids.Count == 0) return;

            await batch(connection, transaction, ids, ct);
            afterId = ids[^1];
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO migration_progress (phase, last_id) VALUES ($phase, $last)
                    ON CONFLICT (phase) DO UPDATE SET last_id = excluded.last_id
                    """;
                command.Parameters.AddWithValue("$phase", phase);
                command.Parameters.AddWithValue("$last", afterId);
                await command.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);

            AfterMigrationBatch?.Invoke(phase);
            if (++batches % 20 == 0)
            {
                _logger.LogInformation("[ReadingDatabase] Schema 2 {Phase} rows up to id {Id}", phase, afterId);
            }
        }
    }

    /// <summary>
    /// Deletes by unit_id, which the (unit_id, block, s) key indexes, so a batch reads only its own units' rows
    /// </summary>
    private static async Task DeleteUnrequestedAnalysisAsync(SqliteConnection connection, SqliteTransaction transaction,
        List<long> unitIds, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM analysis
            WHERE unit_id = $unit
              AND (+state = 'idle' OR NOT EXISTS (
                  SELECT 1 FROM unit u JOIN analysis_request r ON r.manga_file_id = u.manga_file_id WHERE u.id = $unit))
            """;
        var unit = command.Parameters.Add("$unit", SqliteType.Integer);
        foreach (var id in unitIds)
        {
            unit.Value = id;
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// A unit already in the compressed form is skipped, so a batch that ran before a restart costs only its reads.
    /// A unit whose blocks can't be read goes back to the tokenize queue without them, so one damaged row can't stop
    /// the upgrade
    /// </summary>
    private async Task RewriteBlocksAsync(SqliteConnection connection, SqliteTransaction transaction,
        List<long> unitIds, CancellationToken ct)
    {
        await using var requeue = connection.CreateCommand();
        requeue.Transaction = transaction;
        requeue.CommandText = """
            DELETE FROM analysis WHERE unit_id = $id;
            UPDATE unit SET blocks = NULL, tokenizer_version = NULL, state = 'pending', attempts = 0 WHERE id = $id;
            """;
        var requeueId = requeue.Parameters.Add("$id", SqliteType.Integer);

        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT blocks FROM unit WHERE id = $id AND blocks IS NOT NULL";
        var readId = read.Parameters.Add("$id", SqliteType.Integer);

        await using var write = connection.CreateCommand();
        write.Transaction = transaction;
        write.CommandText = "UPDATE unit SET blocks = $blocks WHERE id = $id";
        var writeId = write.Parameters.Add("$id", SqliteType.Integer);
        var blocks = write.Parameters.Add("$blocks", SqliteType.Blob);

        // One unit's blocks in memory at a time
        foreach (var id in unitIds)
        {
            readId.Value = id;
            if (await read.ExecuteScalarAsync(ct) is not { } value || ReadingBlocks.IsCurrent(value)) continue;

            byte[] encoded;
            try
            {
                encoded = ReadingBlocks.Encode(ReadingBlocks.Decode(value, id));
            }
            catch (InvalidDataException ex)
            {
                _logger.LogWarning(ex, "[ReadingDatabase] {Message}, so the page will be tokenized again", ex.Message);
                requeueId.Value = id;
                await requeue.ExecuteNonQueryAsync(ct);
                continue;
            }

            writeId.Value = id;
            blocks.Value = encoded;
            await write.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// A word whose readings or senses aren't valid JSON is left as it is, so one damaged row can't stop the upgrade
    /// </summary>
    private async Task RewriteWordsAsync(SqliteConnection connection, SqliteTransaction transaction,
        List<long> wordIds, CancellationToken ct)
    {
        var rows = new List<(long Id, string Readings, string Senses)>(wordIds.Count);
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT id, readings, senses FROM word WHERE id >= $first AND id <= $last";
            read.Parameters.AddWithValue("$first", wordIds[0]);
            read.Parameters.AddWithValue("$last", wordIds[^1]);
            await using var reader = await read.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        }

        await using var write = connection.CreateCommand();
        write.Transaction = transaction;
        write.CommandText = "UPDATE word SET readings = $readings, senses = $senses WHERE id = $id";
        var id = write.Parameters.Add("$id", SqliteType.Integer);
        var readings = write.Parameters.Add("$readings", SqliteType.Text);
        var senses = write.Parameters.Add("$senses", SqliteType.Text);
        foreach (var row in rows)
        {
            string newReadings, newSenses;
            try
            {
                newReadings = ReadingJson.Reencode(row.Readings);
                newSenses = ReadingJson.Reencode(row.Senses);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "[ReadingDatabase] Word {WordId} has readings or senses that aren't valid JSON, " +
                                       "so it's left as it is", row.Id);
                continue;
            }
            if (newReadings == row.Readings && newSenses == row.Senses) continue;

            id.Value = row.Id;
            readings.Value = newReadings;
            senses.Value = newSenses;
            await write.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Gives the file's free pages back to the file system. VACUUM can't run in a transaction and needs free disk
    /// about the size of the file. A failure leaves a larger file that works the same, so it's logged and not thrown
    /// </summary>
    private async Task VacuumAsync(SqliteConnection connection, CancellationToken ct)
    {
        var before = new FileInfo(DatabasePath).Length;
        _logger.LogInformation("[ReadingDatabase] Compacting {Path}, {Megabytes:0} MB", DatabasePath, before / 1e6);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "VACUUM; PRAGMA wal_checkpoint(TRUNCATE);";
            await command.ExecuteNonQueryAsync(ct);
            _logger.LogInformation("[ReadingDatabase] Compacted {Path} to {Megabytes:0} MB", DatabasePath,
                new FileInfo(DatabasePath).Length / 1e6);
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(ex, "[ReadingDatabase] Couldn't compact {Path}, so it keeps its free pages", DatabasePath);
        }
    }
}
