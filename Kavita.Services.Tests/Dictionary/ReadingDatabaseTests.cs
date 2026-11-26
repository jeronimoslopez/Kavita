using Kavita.API.Services;
using Kavita.Models.DTOs.Dictionary;
using Kavita.Services.Dictionary;
using Microsoft.Data.Sqlite;

namespace Kavita.Services.Tests.Dictionary;

public class ReadingDatabaseTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public ReadingDatabaseTests()
    {
        _directory = Path.Join(Path.GetTempPath(), "kavita-reading-tests", Guid.NewGuid().ToString("N"));
        _path = Path.Join(_directory, "reading", "reading.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
            // Best effort, the OS temp folder gets cleaned eventually
        }
        GC.SuppressFinalize(this);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task NonQueryAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> TablesAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names;
    }

    private static readonly string[] AllTables =
        ["analysis", "analysis_request", "reading_mark", "unit", "word", "word_use"];

    private async Task CreateRawFileAsync(int userVersion)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await using var connection = new SqliteConnection($"Data Source={_path}");
        await connection.OpenAsync();
        await ScalarAsync(connection, $"PRAGMA user_version = {userVersion}");
    }

    [Fact]
    public async Task Open_CreatesTheFolderFileAndSchemaInWalMode()
    {
        var database = new ReadingDatabase(_path);

        await using var connection = await database.OpenConnectionAsync();

        Assert.True(File.Exists(_path));
        Assert.Equal("wal", await ScalarAsync(connection, "PRAGMA journal_mode"));
        Assert.Equal((long) ReadingDatabase.SchemaVersion, await ScalarAsync(connection, "PRAGMA user_version"));
        Assert.Equal(AllTables, await TablesAsync(connection));
    }

    [Fact]
    public async Task Open_MigratesAFileAtVersionZeroWithNoTables()
    {
        await CreateRawFileAsync(0);

        var database = new ReadingDatabase(_path);
        await using var connection = await database.OpenConnectionAsync();

        Assert.Equal((long) ReadingDatabase.SchemaVersion, await ScalarAsync(connection, "PRAGMA user_version"));
        Assert.Equal(AllTables, await TablesAsync(connection));
    }

    [Fact]
    public async Task Open_ReopensAnExistingFileAndKeepsItsRows()
    {
        var store = new ReadingStore(new ReadingDatabase(_path));
        await store.SeedFileAsync(4, 2, "stamp", ReadingPriority.WholeBook);
        SqliteConnection.ClearAllPools();

        var reopened = new ReadingStore(new ReadingDatabase(_path));

        Assert.Equal("stamp", (await reopened.GetFileStampsAsync())[4]);
        Assert.NotNull(await reopened.GetUnitAsync(4, 1));
    }

    [Fact]
    public async Task Open_RefusesAFileFromANewerSchema()
    {
        await CreateRawFileAsync(ReadingDatabase.SchemaVersion + 1);

        var database = new ReadingDatabase(_path);

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.OpenConnectionAsync());
    }

    #region Version 2 upgrade

    private const int RequestedFile = 1;
    private const int UnrequestedFile = 2;
    private const int UnitsPerFile = 30;
    private const string PageText = "吾輩は猫である。名前はまだ無い。どこで生れたかとんと見当がつかぬ。";

    /// <summary>
    /// Escaped JSON as the version 1 store wrote it, with one token per block over the word
    /// </summary>
    private static string EscapedBlocks(long wordId) => System.Text.Json.JsonSerializer.Serialize(
        Enumerable.Range(0, 20).Select(i => new
        {
            i,
            text = PageText,
            tokens = new object[] { new object?[] { 0, 2, wordId, "わがはい", null } },
        }));

    /// <summary>
    /// A version 1 file with escaped JSON. File 1 has a request, pending and done rows and one idle row. File 2 has no
    /// request and an idle row per word occurrence, as the version 1 store left every book
    /// </summary>
    private async Task CreateVersion1Async()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await using var connection = new SqliteConnection($"Data Source={_path};Pooling=False");
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ReadingDatabase.Version1Schema + """

            PRAGMA user_version = 1;
            INSERT INTO word (id, lemma, pos, is_proper, reading, jlpt, readings, senses, lexicon_version)
            VALUES (1, '吾輩', '代名詞', 0, 'わがはい', 1, $readings, $senses, 'lex');
            INSERT INTO analysis_request (manga_file_id, priority, requested_utc) VALUES (1, 0, 0);
            """;
        command.Parameters.AddWithValue("$readings", System.Text.Json.JsonSerializer.Serialize(new[] { "わがはい", "わがい" }));
        command.Parameters.AddWithValue("$senses", System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { key = "1:1", pos = new[] { "代名詞" }, gloss = new[] { "私（男性語）" } },
            new { key = "1:2", pos = new[] { "代名詞" }, gloss = new[] { "我々" } },
        }));
        await command.ExecuteNonQueryAsync();

        foreach (var file in new[] { RequestedFile, UnrequestedFile })
        {
            for (var page = 0; page < UnitsPerFile; page++)
            {
                await using var unit = connection.CreateCommand();
                unit.Transaction = transaction;
                unit.CommandText = """
                    INSERT INTO unit (kind, manga_file_id, page, source_stamp, tokenizer_version, state, priority, requested_utc, blocks)
                    VALUES ('epub', $file, $page, 's', 'v', 'done', 0, 0, $blocks) RETURNING id
                    """;
                unit.Parameters.AddWithValue("$file", file);
                unit.Parameters.AddWithValue("$page", page);
                unit.Parameters.AddWithValue("$blocks", EscapedBlocks(1));
                var unitId = Convert.ToInt64(await unit.ExecuteScalarAsync());

                await using var rows = connection.CreateCommand();
                rows.Transaction = transaction;
                rows.CommandText = file == RequestedFile
                    ? """
                      INSERT INTO analysis (unit_id, block, s, e, word_id, state, priority, requested_utc, sense_key, reading, generator, prompt_version)
                      VALUES ($unit, 0, 0, 2, 1, 'done', 0, 0, '1:2', 'わがはい', 'gen', 'p1'),
                             ($unit, 1, 0, 2, 1, 'pending', 0, 0, NULL, NULL, NULL, NULL),
                             ($unit, 2, 0, 2, 1, 'idle', 0, 0, NULL, NULL, NULL, NULL)
                      """
                    : """
                      WITH RECURSIVE n(b) AS (SELECT 0 UNION ALL SELECT b + 1 FROM n WHERE b < 199)
                      INSERT INTO analysis (unit_id, block, s, e, word_id, state, priority, requested_utc)
                      SELECT $unit, b, 0, 2, 1, 'idle', 0, 0 FROM n
                      """;
                rows.Parameters.AddWithValue("$unit", unitId);
                await rows.ExecuteNonQueryAsync();
            }
        }
        await transaction.CommitAsync();
    }

    private async Task AssertUpgradedAsync(ReadingDatabase database, long sizeBefore)
    {
        await using var connection = await database.OpenConnectionAsync();
        Assert.Equal((long) ReadingDatabase.SchemaVersion, await ScalarAsync(connection, "PRAGMA user_version"));
        Assert.Equal(AllTables, await TablesAsync(connection));

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM analysis WHERE state = 'idle'"));
        Assert.Equal(0L, await ScalarAsync(connection, $"""
            SELECT COUNT(*) FROM analysis a JOIN unit u ON u.id = a.unit_id WHERE u.manga_file_id = {UnrequestedFile}
            """));

        var store = new ReadingStore(database);
        for (var page = 0; page < UnitsPerFile; page++)
        {
            var unit = (await store.GetUnitAsync(RequestedFile, page))!;
            var rows = await store.GetUnitAnalysisAsync(unit.Id);
            Assert.Equal([(0, AnalysisState.Done, "1:2", "gen"), (1, AnalysisState.Pending, null, null)],
                rows.Select(r => (r.Block, r.State, r.SenseKey, r.Generator)));

            Assert.Equal(20, unit.Blocks.Count);
            Assert.All(unit.Blocks, b =>
            {
                Assert.Equal(PageText, b.Text);
                Assert.Equal(new TokenDto(0, 2, 1, "わがはい", null), Assert.Single(b.Tokens));
            });
        }

        Assert.Equal(0L, await ScalarAsync(connection,
            $"SELECT COUNT(*) FROM unit WHERE typeof(blocks) <> 'blob' OR hex(substr(blocks, 1, 1)) <> '{ReadingBlocks.BrotliJson:X2}'"));
        Assert.Contains("わがはい", (string) (await ScalarAsync(connection, "SELECT readings FROM word"))!);
        Assert.Contains("我々", (string) (await ScalarAsync(connection, "SELECT senses FROM word"))!);
        Assert.Equal(["1:1", "1:2"], (await store.GetWordsAsync([1]))[1].Senses.Select(s => s.Key));

        Assert.True(new FileInfo(_path).Length < sizeBefore / 2,
            $"{new FileInfo(_path).Length} bytes after, {sizeBefore} before");
    }

    [Fact]
    public async Task Upgrade_FromVersion1_DropsUnrequestedRowsCompressesBlocksAndShrinksTheFile()
    {
        await CreateVersion1Async();
        var sizeBefore = new FileInfo(_path).Length;

        var database = new ReadingDatabase(_path) { MigrationUnitsPerBatch = 7, VacuumFloorBytes = 0 };
        await database.InitializeAsync();

        await AssertUpgradedAsync(database, sizeBefore);
    }

    [Theory]
    [InlineData("analysis", 3)]
    [InlineData("unit", 2)]
    [InlineData("word", 1)]
    public async Task Upgrade_InterruptedAfterSomeBatches_FinishesOnTheNextOpen(string phase, int batches)
    {
        await CreateVersion1Async();
        var sizeBefore = new FileInfo(_path).Length;
        var seen = 0;
        var crashing = new ReadingDatabase(_path)
        {
            MigrationUnitsPerBatch = 5,
            AfterMigrationBatch = p =>
            {
                if (p == phase && ++seen == batches) throw new IOException("process died");
            },
        };

        await Assert.ThrowsAsync<IOException>(() => crashing.InitializeAsync());
        SqliteConnection.ClearAllPools();
        await using (var raw = new SqliteConnection($"Data Source={_path};Pooling=False"))
        {
            await raw.OpenAsync();
            Assert.Equal(1L, await ScalarAsync(raw, "PRAGMA user_version"));
            Assert.NotNull(await ScalarAsync(raw, $"SELECT last_id FROM migration_progress WHERE phase = '{phase}'"));
            if (phase == "unit")
            {
                // Two batches of five went in before the crash, and the other fifty units are still escaped text
                Assert.Equal(10L, await ScalarAsync(raw, "SELECT COUNT(*) FROM unit WHERE typeof(blocks) = 'blob'"));
                Assert.Equal(50L, await ScalarAsync(raw, "SELECT COUNT(*) FROM unit WHERE typeof(blocks) = 'text'"));
            }
        }

        var reopened = new ReadingDatabase(_path) { MigrationUnitsPerBatch = 5, VacuumFloorBytes = 0 };
        await reopened.InitializeAsync();

        await AssertUpgradedAsync(reopened, sizeBefore);
    }

    [Fact]
    public async Task Upgrade_SendsAUnitWithUnreadableBlocksBackToTheQueue()
    {
        await CreateVersion1Async();
        await using (var raw = new SqliteConnection($"Data Source={_path};Pooling=False"))
        {
            await raw.OpenAsync();
            await ScalarAsync(raw, $"UPDATE unit SET blocks = '[{{\"i\": 0, \"text\"' WHERE manga_file_id = {RequestedFile} AND page = 3");
        }

        var database = new ReadingDatabase(_path);
        await database.InitializeAsync();

        var unit = (await new ReadingStore(database).GetUnitAsync(RequestedFile, 3))!;
        Assert.Equal(ReadingUnitState.Pending, unit.State);
        Assert.Empty(unit.Blocks);
        Assert.Empty(await new ReadingStore(database).GetUnitAnalysisAsync(unit.Id));
        Assert.NotEmpty((await new ReadingStore(database).GetUnitAsync(RequestedFile, 4))!.Blocks);
    }

    [Fact]
    public async Task Upgrade_LeavesAWordWithUnreadableJsonAsItIs()
    {
        await CreateVersion1Async();
        await using (var raw = new SqliteConnection($"Data Source={_path};Pooling=False"))
        {
            await raw.OpenAsync();
            await ScalarAsync(raw, """
                INSERT INTO word (id, lemma, pos, is_proper, reading, jlpt, readings, senses, lexicon_version)
                VALUES (2, '壊', '名詞', 0, 'こわ', NULL, '["こわ"]', '[{"key": "2:1"', 'lex');
                """);
        }

        var database = new ReadingDatabase(_path);
        await database.InitializeAsync();

        await using var connection = await database.OpenConnectionAsync();
        Assert.Equal((long) ReadingDatabase.SchemaVersion, await ScalarAsync(connection, "PRAGMA user_version"));
        Assert.Equal("""["こわ"]""", await ScalarAsync(connection, "SELECT readings FROM word WHERE id = 2"));
        Assert.Equal("[{\"key\": \"2:1\"", await ScalarAsync(connection, "SELECT senses FROM word WHERE id = 2"));
        Assert.Contains("わがはい", (string) (await ScalarAsync(connection, "SELECT readings FROM word WHERE id = 1"))!);
    }

    /// <summary>
    /// A version 2 file of about 8 MB. With <paramref name="free"/> most of its pages are free afterwards, as a
    /// compaction cut short would leave it
    /// </summary>
    private async Task<long> CreateVersion2Async(bool free)
    {
        var database = new ReadingDatabase(_path);
        var store = new ReadingStore(database);
        await store.SeedFileAsync(1, 40, "s", 0);
        var text = string.Concat(Enumerable.Range(0, 20_000).Select(i => (char) (0x4E00 + (i * 7919) % 20_000)));
        for (var page = 0; page < 40; page++)
        {
            var unit = (await store.GetUnitAsync(1, page))!;
            Assert.True(await store.SaveTokenizedUnitAsync(unit.Id, "s", "v", [new StoredBlock(0, text + page, [])]));
        }
        await using (var connection = await database.OpenConnectionAsync())
        {
            if (free) await ScalarAsync(connection, "DELETE FROM unit WHERE page >= 4");
            await ScalarAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
        }
        SqliteConnection.ClearAllPools();
        return new FileInfo(_path).Length;
    }

    private async Task<long> FreePagesAsync(ReadingDatabase database)
    {
        await using var connection = await database.OpenConnectionAsync();
        return (long) (await ScalarAsync(connection, "PRAGMA freelist_count"))!;
    }

    [Fact]
    public async Task Open_CompactsAVersion2FileWithManyFreePages()
    {
        var before = await CreateVersion2Async(free: true);

        var database = new ReadingDatabase(_path) { VacuumFloorBytes = 1_000_000 };
        await database.InitializeAsync();

        Assert.Equal(0L, await FreePagesAsync(database));
        Assert.True(new FileInfo(_path).Length < before / 2, $"{new FileInfo(_path).Length} bytes after, {before} before");
        Assert.NotNull(await new ReadingStore(database).GetUnitAsync(1, 3));
    }

    [Theory]
    [InlineData(false, 1_000_000)]
    [InlineData(true, 50_000_000)]
    public async Task Open_LeavesAFileWithFewFreePagesOrUnderTheFloorAlone(bool free, long floor)
    {
        var before = await CreateVersion2Async(free);
        long freeBefore;
        await using (var raw = new SqliteConnection($"Data Source={_path};Pooling=False"))
        {
            await raw.OpenAsync();
            freeBefore = (long) (await ScalarAsync(raw, "PRAGMA freelist_count"))!;
        }

        var database = new ReadingDatabase(_path) { VacuumFloorBytes = floor };
        await database.InitializeAsync();

        Assert.Equal(freeBefore, await FreePagesAsync(database));
        Assert.Equal(before, new FileInfo(_path).Length);
    }

    [Fact]
    public async Task Upgrade_TurnsOtherCallersAwayUntilItsDone()
    {
        await CreateVersion1Async();
        using var entered = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        var database = new ReadingDatabase(_path)
        {
            AfterMigrationBatch = _ =>
            {
                entered.Release();
                release.Wait();
            },
        };
        var store = new ReadingStore(database);

        var upgrade = database.InitializeAsync();
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(30)));

        await Assert.ThrowsAsync<ReadingStoreNotReadyException>(() => database.OpenConnectionAsync());
        await Assert.ThrowsAsync<ReadingStoreNotReadyException>(() => store.GetFileStampsAsync());
        Assert.Null(await store.GetUnitAsync(RequestedFile, 0));

        release.Release(100);
        await upgrade;
        Assert.NotNull(await store.GetUnitAsync(RequestedFile, 0));
    }

    #endregion

    #region Version 3 upgrade

    [Fact]
    public async Task Upgrade_FromVersion2_AddsEmptyMarkTablesAndKeepsEveryRow()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await using (var raw = new SqliteConnection($"Data Source={_path};Pooling=False"))
        {
            await raw.OpenAsync();
            await NonQueryAsync(raw, ReadingDatabase.Version1Schema + """

                PRAGMA user_version = 2;
                INSERT INTO unit (kind, manga_file_id, page, source_stamp, tokenizer_version, state, priority, requested_utc, blocks)
                VALUES ('epub', 1, 0, 's', 'v', 'done', 0, 0, '[{"i": 0, "text": "猫", "tokens": [[0, 1, null, null, null]]}]');
                """);
        }

        var database = new ReadingDatabase(_path);
        await database.InitializeAsync();

        await using var connection = await database.OpenConnectionAsync();
        Assert.Equal(3L, await ScalarAsync(connection, "PRAGMA user_version"));
        Assert.Equal(AllTables, await TablesAsync(connection));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM reading_mark"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM word_use"));
        // Blocks stored before ruby read back with none, and the page keeps its state until it's tokenized again
        var unit = (await new ReadingStore(database).GetUnitAsync(1, 0))!;
        Assert.Equal(ReadingUnitState.Done, unit.State);
        Assert.Equal("猫", Assert.Single(unit.Blocks).Text);
        Assert.Empty(unit.Blocks[0].Ruby);
    }

    [Fact]
    public async Task Upgrade_FromVersion2_DoesNotTurnCallersAway()
    {
        await CreateRawFileAsync(0);
        await new ReadingDatabase(_path).InitializeAsync();
        SqliteConnection.ClearAllPools();
        await using var raw = new SqliteConnection($"Data Source={_path};Pooling=False");
        await raw.OpenAsync();
        await NonQueryAsync(raw, "DROP TABLE reading_mark; DROP TABLE word_use; PRAGMA user_version = 2;");

        // Another writer holds the lock, so the step waits on it and a caller arrives while it runs
        var blocker = raw.BeginTransaction(deferred: false);
        var database = new ReadingDatabase(_path);
        var upgrade = database.InitializeAsync();
        var caller = database.OpenConnectionAsync();
        await Task.WhenAny(caller, Task.Delay(500));

        Assert.False(caller.IsCompleted, "The caller was answered before the upgrade finished");
        await blocker.CommitAsync();
        await blocker.DisposeAsync();
        await upgrade;
        await using var connection = await caller;
        Assert.Equal(3L, await ScalarAsync(connection, "PRAGMA user_version"));
        Assert.Equal(AllTables, await TablesAsync(connection));
    }

    #endregion

    [Theory]
    [InlineData(ReadingStore.NextPendingUnitSql, "ix_unit_queue")]
    [InlineData(ReadingStore.PendingAnalysisSql, "ix_analysis_queue")]
    public async Task QueueQueries_ReadTheirIndexWithoutSorting(string sql, string index)
    {
        var plan = await PlanAsync(sql);

        Assert.Contains(plan, step => step.Contains(index));
        Assert.DoesNotContain(plan, step => step.Contains("TEMP B-TREE"));
    }

    public static TheoryData<string, string, string> TargetedUpdates => new()
    {
        { ReadingStore.RaiseUnitPrioritySql, "sqlite_autoindex_unit_1", "ix_unit_queue" },
        { ReadingStore.RaiseAnalysisPrioritySql, "sqlite_autoindex_analysis_1", "ix_analysis_queue" },
        { ReadingStore.RefreshUnitsSql, "sqlite_autoindex_unit_1", "ix_unit_queue" },
    };

    [Theory]
    [MemberData(nameof(TargetedUpdates))]
    public async Task TargetedUpdates_FindRowsByFileOrUnitAndNotByQueue(string sql, string index, string queueIndex)
    {
        var plan = await PlanAsync(sql);

        Assert.Contains(plan, step => step.StartsWith("SEARCH unit") && step.Contains("sqlite_autoindex_unit_1"));
        Assert.Contains(plan, step => step.Contains(index));
        Assert.DoesNotContain(plan, step => step.Contains(queueIndex));
        Assert.DoesNotContain(plan, step => step.StartsWith("SCAN"));
    }

    public static TheoryData<string, string> MarkTotals => new()
    {
        // One file, as the one-file overload asks
        { ReadingStore.ReadingMarkTotalsSql("$f0", "$w0, $w1, $w2"), "SEARCH m USING PRIMARY KEY (unit_id=? AND word_id=?)" },
        { ReadingStore.WordUseTotalsSql("$f0", "$w0, $w1, $w2"), "SEARCH w USING PRIMARY KEY (unit_id=? AND word_id=?)" },
        // Several files
        { ReadingStore.ReadingMarkTotalsSql("$f0, $f1", "$w0, $w1, $w2"), "SEARCH m USING PRIMARY KEY (unit_id=? AND word_id=?)" },
        { ReadingStore.WordUseTotalsSql("$f0, $f1", "$w0, $w1, $w2"), "SEARCH w USING PRIMARY KEY (unit_id=? AND word_id=?)" },
    };

    [Theory]
    [MemberData(nameof(MarkTotals))]
    public async Task MarkTotals_ReadTheFilesUnitsAndThenEachUnitsRowsForTheWords(string sql, string rows)
    {
        var plan = await PlanAsync(sql);

        Assert.Contains(plan, step => step.StartsWith("SEARCH u USING") &&
                                      step.EndsWith("INDEX sqlite_autoindex_unit_1 (kind=? AND manga_file_id=?)"));
        Assert.Contains(rows, plan);
        Assert.DoesNotContain(plan, step => step.StartsWith("SCAN"));
    }

    [Fact]
    public async Task SavingAPage_ReplacesItsRowsThroughTheKey()
    {
        var plan = await PlanAsync("DELETE FROM reading_mark WHERE unit_id = $unit");
        plan.AddRange(await PlanAsync("DELETE FROM word_use WHERE unit_id = $unit"));

        Assert.Equal(["SEARCH reading_mark USING PRIMARY KEY (unit_id=?)", "SEARCH word_use USING PRIMARY KEY (unit_id=?)"], plan);
    }

    private async Task<List<string>> PlanAsync(string sql)
    {
        var database = new ReadingDatabase(_path);
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        foreach (var name in new[] { "$minPriority", "$limit", "$priority", "$now", "$file", "$page", "$block", "$f0", "$f1", "$version",
                     "$w0", "$w1", "$w2", "$unit" })
        {
            command.Parameters.AddWithValue(name, 1);
        }
        command.Parameters.AddWithValue("$kind", "epub");
        command.Parameters.AddWithValue("$state", "pending");

        var plan = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) plan.Add(reader.GetString(3));
        return plan;
    }
}
