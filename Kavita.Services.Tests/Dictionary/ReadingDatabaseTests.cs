using Kavita.API.Services;
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

    private static async Task<List<string>> TablesAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names;
    }

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
        Assert.Equal(["analysis", "analysis_request", "unit", "word"], await TablesAsync(connection));
    }

    [Fact]
    public async Task Open_MigratesAFileAtVersionZeroWithNoTables()
    {
        await CreateRawFileAsync(0);

        var database = new ReadingDatabase(_path);
        await using var connection = await database.OpenConnectionAsync();

        Assert.Equal((long) ReadingDatabase.SchemaVersion, await ScalarAsync(connection, "PRAGMA user_version"));
        Assert.Equal(["analysis", "analysis_request", "unit", "word"], await TablesAsync(connection));
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
        { ReadingStore.QueueRequestedAnalysisSql(2), "sqlite_autoindex_analysis_1", "ix_analysis_queue" },
        { ReadingStore.RaiseRequestedAnalysisSql(2), "sqlite_autoindex_analysis_1", "ix_analysis_queue" },
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

    private async Task<List<string>> PlanAsync(string sql)
    {
        var database = new ReadingDatabase(_path);
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        foreach (var name in new[] { "$minPriority", "$limit", "$priority", "$now", "$file", "$page", "$block", "$f0", "$f1" })
        {
            command.Parameters.AddWithValue(name, 1);
        }
        command.Parameters.AddWithValue("$kind", "epub");

        var plan = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) plan.Add(reader.GetString(3));
        return plan;
    }
}
