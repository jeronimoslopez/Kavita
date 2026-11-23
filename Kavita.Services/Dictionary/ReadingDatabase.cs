using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Kavita.Services.Dictionary;

/// <summary>
/// Owns config/reading/reading.db, the SQLite file behind <see cref="ReadingStore"/>. It's kept out of kavita.db so
/// tokens and analysis stay out of Kavita's backups, and the tokenizer never waits behind kavita.db's writer
/// </summary>
/// <remarks>
/// The schema version is <c>PRAGMA user_version</c>. Step n of <see cref="Migrations"/> takes the file from version n
/// to n + 1, so a schema change is one appended step and never an edit to an old one
/// </remarks>
public class ReadingDatabase
{
    /// <summary>
    /// Unit and analysis ids are AUTOINCREMENT so a deleted id never comes back. The worker and the runner hold ids
    /// across calls, and a reused id would let an answer or a tokenize land on a different row. Queue times are UTC
    /// ticks. Each queue index runs (state, priority DESC, requested_utc, id), the order
    /// IReadingStore documents, so taking the next row reads the front of the index
    /// </summary>
    private static readonly IReadOnlyList<string> Migrations =
    [
        """
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
        """,
    ];

    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    public ReadingDatabase(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
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
    /// Opens a pooled connection, creating the file and bringing its schema up to date on first use
    /// </summary>
    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct);
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        // Several services and the tokenize worker write at once, and the busy timeout makes a second writer wait
        // for the first instead of failing with SQLITE_BUSY
        command.CommandText = "PRAGMA busy_timeout = 10000; PRAGMA synchronous = NORMAL; PRAGMA temp_store = MEMORY;";
        await command.ExecuteNonQueryAsync(ct);
        return connection;
    }

    private async Task EnsureCreatedAsync(CancellationToken ct)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct);
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 10000;";
                await command.ExecuteNonQueryAsync(ct);
            }

            await MigrateAsync(connection, ct);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static async Task MigrateAsync(SqliteConnection connection, CancellationToken ct)
    {
        // The version is read again inside the write transaction, so a second process opening the same file can't
        // run a step twice
        await using var transaction = (SqliteTransaction) await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(await command.ExecuteScalarAsync(ct));
        if (version > Migrations.Count)
        {
            throw new InvalidOperationException(
                $"reading.db is at schema version {version}, and this build only knows up to {Migrations.Count}");
        }

        for (var step = version; step < Migrations.Count; step++)
        {
            command.CommandText = Migrations[step] + $"\nPRAGMA user_version = {step + 1};";
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }
}
