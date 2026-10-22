using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Kavita.Services.Yomitan;

/// <summary>
/// Owns the separate SQLite file that holds the Yomitan dictionaries. The tables copy Yomitan's IndexedDB object stores
/// (<c>ext/js/dictionary/dictionary-database.js</c>) and carry the same indexes. It is kept out of kavita.db so hundreds of
/// megabytes of dictionary rows stay out of Kavita's backups and EF migrations.
/// </summary>
public class YomitanDictionaryDatabase
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS dictionaries (
            id INTEGER PRIMARY KEY,
            title TEXT NOT NULL,
            revision TEXT NOT NULL,
            version INTEGER NOT NULL,
            importSuccess INTEGER NOT NULL DEFAULT 0,
            sourceFile TEXT NULL,
            summary TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_dictionaries_title ON dictionaries (title);
        CREATE INDEX IF NOT EXISTS ix_dictionaries_version ON dictionaries (version);

        CREATE TABLE IF NOT EXISTS terms (
            id INTEGER PRIMARY KEY,
            dictionary TEXT NOT NULL,
            expression TEXT NOT NULL,
            reading TEXT NOT NULL,
            expressionReverse TEXT NULL,
            readingReverse TEXT NULL,
            definitionTags TEXT NULL,
            rules TEXT NULL,
            score REAL NOT NULL DEFAULT 0,
            glossary TEXT NOT NULL,
            sequence INTEGER NULL,
            termTags TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_terms_expression ON terms (expression);
        CREATE INDEX IF NOT EXISTS ix_terms_reading ON terms (reading);
        CREATE INDEX IF NOT EXISTS ix_terms_expressionReverse ON terms (expressionReverse);
        CREATE INDEX IF NOT EXISTS ix_terms_readingReverse ON terms (readingReverse);
        CREATE INDEX IF NOT EXISTS ix_terms_dictionary_sequence ON terms (dictionary, sequence);

        CREATE TABLE IF NOT EXISTS termMeta (
            id INTEGER PRIMARY KEY,
            dictionary TEXT NOT NULL,
            expression TEXT NOT NULL,
            mode TEXT NOT NULL,
            data TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_termMeta_expression ON termMeta (expression);
        CREATE INDEX IF NOT EXISTS ix_termMeta_dictionary ON termMeta (dictionary);

        CREATE TABLE IF NOT EXISTS kanji (
            id INTEGER PRIMARY KEY,
            dictionary TEXT NOT NULL,
            character TEXT NOT NULL,
            onyomi TEXT NULL,
            kunyomi TEXT NULL,
            tags TEXT NULL,
            meanings TEXT NOT NULL,
            stats TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_kanji_character ON kanji (character);
        CREATE INDEX IF NOT EXISTS ix_kanji_dictionary ON kanji (dictionary);

        CREATE TABLE IF NOT EXISTS kanjiMeta (
            id INTEGER PRIMARY KEY,
            dictionary TEXT NOT NULL,
            character TEXT NOT NULL,
            mode TEXT NOT NULL,
            data TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_kanjiMeta_character ON kanjiMeta (character);
        CREATE INDEX IF NOT EXISTS ix_kanjiMeta_dictionary ON kanjiMeta (dictionary);

        CREATE TABLE IF NOT EXISTS tagMeta (
            id INTEGER PRIMARY KEY,
            dictionary TEXT NOT NULL,
            name TEXT NOT NULL,
            category TEXT NULL,
            "order" REAL NOT NULL DEFAULT 0,
            notes TEXT NULL,
            score REAL NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS ix_tagMeta_name ON tagMeta (name);
        CREATE INDEX IF NOT EXISTS ix_tagMeta_dictionary ON tagMeta (dictionary);

        CREATE TABLE IF NOT EXISTS media (
            id INTEGER PRIMARY KEY,
            dictionary TEXT NOT NULL,
            path TEXT NOT NULL,
            mediaType TEXT NOT NULL,
            width INTEGER NOT NULL,
            height INTEGER NOT NULL,
            content BLOB NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_media_path ON media (path);
        CREATE INDEX IF NOT EXISTS ix_media_dictionary ON media (dictionary);
        """;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    public YomitanDictionaryDatabase(string databasePath)
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
    /// config/yomitan/dictionaries.db, resolved against the working directory the same way DirectoryService resolves config/
    /// </summary>
    public static string DefaultDatabasePath =>
        Path.Join(Directory.GetCurrentDirectory(), "config", "yomitan", "dictionaries.db");

    /// <summary>
    /// Folder of zips copied next to the server binaries at build time
    /// </summary>
    public static string BundledDictionaryDirectory => Path.Join(AppContext.BaseDirectory, "Dictionaries");

    public string DatabasePath { get; }

    /// <summary>
    /// Opens a pooled connection, creating the file and schema on first use
    /// </summary>
    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct);
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        // Readers never block on the importer under WAL, the busy timeout covers the brief checkpoint locks
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
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode = WAL;" + Schema;
            await command.ExecuteNonQueryAsync(ct);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }
}
