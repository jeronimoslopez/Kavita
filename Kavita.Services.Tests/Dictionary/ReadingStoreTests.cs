using Kavita.API.Services;
using Kavita.Models.DTOs.Dictionary;
using Kavita.Services.Dictionary;
using Microsoft.Data.Sqlite;

namespace Kavita.Services.Tests.Dictionary;

public class ReadingStoreTests : IDisposable
{
    private const string Stamp = "stamp-1";
    private const string NewStamp = "stamp-2";
    private const string Lexicon = "lex-1";
    private const string Tokenizer = "sidecar=1";

    private readonly string _directory;
    private readonly ReadingDatabase _database;
    private readonly ManualTime _time = new();
    private readonly ReadingStore _store;

    public ReadingStoreTests()
    {
        _directory = Path.Join(Path.GetTempPath(), "kavita-reading-tests", Guid.NewGuid().ToString("N"));
        // The database creates its own folder, so this one stays absent until the first connection
        _database = new ReadingDatabase(Path.Join(_directory, "reading", "reading.db"));
        _store = new ReadingStore(_database, _time);
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

    #region Fixture

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;

        public void Advance() => Now = Now.AddSeconds(1);
    }

    private static SidecarWordDto Word(string lemma, int senses = 1, int readings = 1, string pos = "noun",
        bool isProper = false, int? jlpt = 3) => new()
    {
        Lemma = lemma,
        Pos = pos,
        IsProper = isProper,
        Reading = lemma + "-r0",
        Jlpt = jlpt,
        Readings = Enumerable.Range(0, readings).Select(i => $"{lemma}-r{i}").ToList(),
        Senses = Enumerable.Range(1, senses).Select(i => new SenseDto($"{lemma}:{i}", ["n"], [$"gloss {i}"])).ToList(),
    };

    /// <summary>
    /// Two ambiguous words and one plain one
    /// </summary>
    private async Task<(long Hot, long Cold, long Plain)> SeedWordsAsync()
    {
        var ids = await _store.UpsertWordsAsync([Word("hot", 2, 2), Word("cold", 3), Word("plain")], Lexicon);
        return (ids[0], ids[1], ids[2]);
    }

    private static StoredBlock Block(int index, string text, params TokenDto[] tokens) => new(index, text, tokens);

    private static TokenDto Token(int s, int e, long? w, string? r = null, IList<SubTokenDto>? sub = null) =>
        new(s, e, w, r, sub);

    private async Task<ReadingUnitRef> NextAsync() =>
        await _store.GetNextPendingUnitAsync() ?? throw new InvalidOperationException("Nothing is pending");

    /// <summary>
    /// Seeds one page of the file and tokenizes it with the given spans
    /// </summary>
    private async Task<long> TokenizedPageAsync(int file, IReadOnlyList<AnalysisSpan> spans, int pageCount = 1,
        string text = "0123456789")
    {
        await _store.SeedFileAsync(file, pageCount, Stamp, ReadingPriority.WholeBook);
        var unit = (await _store.GetUnitAsync(file, 0))!;
        var blocks = spans.Select(s => s.Block).Append(0).Distinct().Order()
            .Select(b => Block(b, $"{text}-{b}")).ToList();
        Assert.True(await _store.SaveTokenizedUnitAsync(unit.Id, Stamp, Tokenizer, blocks, spans));
        return unit.Id;
    }

    private async Task<SqliteConnection> OpenAsync() => await _database.OpenConnectionAsync();

    private async Task<long> ScalarAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    #endregion

    #region Words

    [Fact]
    public async Task UpsertWords_ReturnsIdsInInputOrder()
    {
        var ids = await _store.UpsertWordsAsync([Word("a"), Word("b"), Word("a", pos: "verb"), Word("a", isProper: true)],
            Lexicon);

        Assert.Equal(4, ids.Distinct().Count());
        var words = await _store.GetWordsAsync(ids);
        Assert.Equal(["a", "b", "a", "a"], ids.Select(id => words[id].Lemma));
        Assert.Equal("verb", words[ids[2]].Pos);
        Assert.True(words[ids[3]].IsProper);
    }

    [Fact]
    public async Task UpsertWords_UpdatesAnExistingWordInPlace()
    {
        var first = await _store.UpsertWordsAsync([Word("a"), Word("b")], Lexicon);
        var changed = Word("a", senses: 2, readings: 3, jlpt: null);
        var second = await _store.UpsertWordsAsync([Word("c"), changed, Word("a")], "lex-2");

        Assert.Equal(first[0], second[1]);
        Assert.Equal(first[0], second[2]);
        Assert.Equal(3, await ScalarAsync("SELECT COUNT(*) FROM word"));

        var word = (await _store.GetWordsAsync([first[0]]))[first[0]];
        // The last write of the same key in one call wins
        Assert.Single(word.Senses);
        Assert.Equal("lex-2", word.LexiconVersion);
        Assert.Equal(3, word.Jlpt);
    }

    [Fact]
    public async Task GetWords_RoundTripsEveryFieldAndLeavesOutMissingIds()
    {
        var input = Word("a", senses: 2, readings: 2, jlpt: null);
        var id = (await _store.UpsertWordsAsync([input], Lexicon))[0];

        var words = await _store.GetWordsAsync([id, id + 100]);

        var word = Assert.Single(words).Value;
        Assert.Equal(id, word.Id);
        Assert.Equal("a", word.Lemma);
        Assert.Equal("noun", word.Pos);
        Assert.False(word.IsProper);
        Assert.Equal("a-r0", word.Reading);
        Assert.Null(word.Jlpt);
        Assert.Equal(input.Readings, word.Readings);
        Assert.Equal(["a:1", "a:2"], word.Senses.Select(s => s.Key));
        Assert.Equal(["gloss 2"], word.Senses[1].Gloss);
        Assert.Equal(["n"], word.Senses[1].Pos);
        Assert.Equal(Lexicon, word.LexiconVersion);
    }

    [Fact]
    public async Task GetWords_ReadsMoreIdsThanOneBatch()
    {
        var ids = await _store.UpsertWordsAsync(Enumerable.Range(0, 1200).Select(i => Word($"w{i}")).ToList(), Lexicon);

        var words = await _store.GetWordsAsync(ids);

        Assert.Equal(1200, words.Count);
        Assert.Equal("w1199", words[ids[1199]].Lemma);
    }

    [Fact]
    public async Task UpsertAndGetWords_AcceptEmptyInput()
    {
        Assert.Empty(await _store.UpsertWordsAsync([], Lexicon));
        Assert.Empty(await _store.GetWordsAsync([]));
    }

    #endregion

    #region Seeding

    [Fact]
    public async Task SeedFile_CreatesAPendingUnitPerPage()
    {
        await _store.SeedFileAsync(7, 3, Stamp, ReadingPriority.NextChapter);

        for (var page = 0; page < 3; page++)
        {
            var unit = await _store.GetUnitAsync(7, page);
            Assert.NotNull(unit);
            Assert.Equal(ReadingUnitState.Pending, unit.State);
            Assert.Equal(ReadingUnitKind.Epub, unit.Kind);
            Assert.Equal(Stamp, unit.SourceStamp);
            Assert.Null(unit.TokenizerVersion);
            Assert.Empty(unit.Blocks);
        }
        Assert.Null(await _store.GetUnitAsync(7, 3));
        Assert.Equal(ReadingPriority.NextChapter, await ScalarAsync("SELECT MIN(priority) FROM unit"));
        Assert.Equal(Stamp, Assert.Single(await _store.GetFileStampsAsync(), kv => kv.Key == 7).Value);
    }

    [Fact]
    public async Task SeedFile_TrimsPagesPastTheEndWithTheirAnalysis()
    {
        var (hot, _, _) = await SeedWordsAsync();
        await _store.SeedFileAsync(1, 3, Stamp, 0);
        var last = (await _store.GetUnitAsync(1, 2))!;
        Assert.True(await _store.SaveTokenizedUnitAsync(last.Id, Stamp, Tokenizer, [Block(0, "abc")],
            [new AnalysisSpan(0, 0, 1, hot)]));

        await _store.SeedFileAsync(1, 2, Stamp, 0);

        Assert.Null(await _store.GetUnitAsync(1, 2));
        Assert.NotNull(await _store.GetUnitAsync(1, 1));
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM analysis"));

        await _store.SeedFileAsync(1, 0, Stamp, 0);
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM unit"));
    }

    [Fact]
    public async Task SeedFile_ANewStampSendsUnitsBackToPendingAndKeepsBlocksAndAnalysis()
    {
        var (hot, _, _) = await SeedWordsAsync();
        var unitId = await TokenizedPageAsync(1, [new AnalysisSpan(0, 0, 3, hot)], pageCount: 2);
        await _store.RecordFailedAttemptAsync((await NextAsync()).Id, 5);

        await _store.SeedFileAsync(1, 2, NewStamp, ReadingPriority.OpenChapter);

        var unit = (await _store.GetUnitAsync(1, 0))!;
        Assert.Equal(unitId, unit.Id);
        Assert.Equal(ReadingUnitState.Pending, unit.State);
        Assert.Equal(NewStamp, unit.SourceStamp);
        Assert.Equal(Tokenizer, unit.TokenizerVersion);
        Assert.Single(unit.Blocks);
        Assert.Single(await _store.GetUnitAnalysisAsync(unitId));
        Assert.Equal(0, await ScalarAsync("SELECT MAX(attempts) FROM unit"));
        Assert.Equal(ReadingPriority.OpenChapter, await ScalarAsync("SELECT MIN(priority) FROM unit"));
        Assert.Equal(NewStamp, (await _store.GetFileStampsAsync())[1]);
    }

    [Fact]
    public async Task SeedFile_TheSameStampLeavesDoneAndFailedUnitsAlone()
    {
        await TokenizedPageAsync(1, [], pageCount: 2);
        var other = await NextAsync();
        Assert.Equal(1, other.Page);
        Assert.Equal(ReadingUnitState.Failed, await _store.RecordFailedAttemptAsync(other.Id, 1));

        await _store.SeedFileAsync(1, 2, Stamp, ReadingPriority.Tapped);

        Assert.Equal(ReadingUnitState.Done, (await _store.GetUnitAsync(1, 0))!.State);
        Assert.Equal(ReadingUnitState.Failed, (await _store.GetUnitAsync(1, 1))!.State);
        Assert.Null(await _store.GetNextPendingUnitAsync());

        await _store.SeedFileAsync(1, 2, NewStamp, ReadingPriority.Tapped);
        Assert.Equal(ReadingUnitState.Pending, (await _store.GetUnitAsync(1, 1))!.State);
    }

    [Fact]
    public async Task GetFileStamps_ListsEveryFileWithUnits()
    {
        await _store.SeedFileAsync(1, 1, Stamp, 0);
        await _store.SeedFileAsync(2, 4, NewStamp, 0);
        await _store.SeedFileAsync(3, 0, Stamp, 0);

        var stamps = await _store.GetFileStampsAsync();

        Assert.Equal(2, stamps.Count);
        Assert.Equal(Stamp, stamps[1]);
        Assert.Equal(NewStamp, stamps[2]);
    }

    [Fact]
    public async Task DeleteFiles_RemovesUnitsAnalysisAndRequestsAndKeepsWords()
    {
        var (hot, _, _) = await SeedWordsAsync();
        await TokenizedPageAsync(1, [new AnalysisSpan(0, 0, 1, hot)]);
        await TokenizedPageAsync(2, [new AnalysisSpan(0, 0, 1, hot)]);
        await _store.RequestAnalysisAsync([1, 2], 0);

        await _store.DeleteFilesAsync([1]);

        Assert.Null(await _store.GetUnitAsync(1, 0));
        Assert.NotNull(await _store.GetUnitAsync(2, 0));
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM analysis"));
        Assert.Equal(2, await ScalarAsync("SELECT manga_file_id FROM analysis_request"));
        Assert.Equal(3, await ScalarAsync("SELECT COUNT(*) FROM word"));

        await _store.DeleteFilesAsync([]);
        Assert.NotNull(await _store.GetUnitAsync(2, 0));
    }

    [Fact]
    public async Task DeleteFiles_HandlesMoreFilesThanOneBatch()
    {
        foreach (var file in Enumerable.Range(1, 3)) await _store.SeedFileAsync(file, 1, Stamp, 0);

        await _store.DeleteFilesAsync(Enumerable.Range(1, 1200).ToList());

        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM unit"));
    }

    #endregion

    #region Tokenization queue

    [Fact]
    public async Task GetNextPendingUnit_OrdersByPriorityThenQueueTimeThenId()
    {
        Assert.Null(await _store.GetNextPendingUnitAsync());

        await _store.SeedFileAsync(1, 2, Stamp, ReadingPriority.WholeBook);
        _time.Advance();
        await _store.SeedFileAsync(2, 1, Stamp, ReadingPriority.NextChapter);
        _time.Advance();
        await _store.SeedFileAsync(3, 1, Stamp, ReadingPriority.NextChapter);

        var order = new List<(int File, int Page)>();
        while (await _store.GetNextPendingUnitAsync() is { } next)
        {
            order.Add((next.MangaFileId, next.Page));
            Assert.Equal(Stamp, next.SourceStamp);
            Assert.Equal(ReadingUnitKind.Epub, next.Kind);
            Assert.True(await _store.SaveTokenizedUnitAsync(next.Id, next.SourceStamp, Tokenizer, [], []));
        }

        Assert.Equal([(2, 0), (3, 0), (1, 0), (1, 1)], order);
    }

    [Fact]
    public async Task SaveTokenizedUnit_StoresBlocksAndMarksTheUnitDone()
    {
        await _store.SeedFileAsync(1, 1, Stamp, 0);
        var next = await NextAsync();
        StoredBlock[] blocks =
        [
            Block(0, "食べ物を", Token(0, 3, 5, "たべもの", [new SubTokenDto(0, 2, 6, "たべ"), new SubTokenDto(2, 3, null, null)]),
                Token(3, 4, null)),
            Block(1, "𠮟る", Token(0, 3, 7, "しかる")),
        ];

        Assert.True(await _store.SaveTokenizedUnitAsync(next.Id, Stamp, Tokenizer, blocks, []));

        var unit = (await _store.GetUnitAsync(1, 0))!;
        Assert.Equal(ReadingUnitState.Done, unit.State);
        Assert.Equal(Tokenizer, unit.TokenizerVersion);
        Assert.Equal(2, unit.Blocks.Count);
        Assert.Equal(1, unit.Blocks[1].Index);
        Assert.Equal("𠮟る", unit.Blocks[1].Text);
        var compound = unit.Blocks[0].Tokens[0];
        Assert.Equal((0, 3, 5L, "たべもの"), (compound.S, compound.E, compound.W!.Value, compound.R));
        Assert.Equal(new SubTokenDto(0, 2, 6, "たべ"), compound.Sub![0]);
        Assert.Equal(new SubTokenDto(2, 3, null, null), compound.Sub[1]);
        Assert.Equal(Token(3, 4, null), unit.Blocks[0].Tokens[1]);
        Assert.Null(await _store.GetNextPendingUnitAsync());
    }

    [Fact]
    public async Task SaveTokenizedUnit_ReturnsFalseAndSavesNothingOnAStaleStampOrMissingUnit()
    {
        var (hot, _, _) = await SeedWordsAsync();
        await _store.SeedFileAsync(1, 1, Stamp, 0);
        var picked = await NextAsync();
        await _store.SeedFileAsync(1, 1, NewStamp, 0);

        Assert.False(await _store.SaveTokenizedUnitAsync(picked.Id, picked.SourceStamp, Tokenizer,
            [Block(0, "abc")], [new AnalysisSpan(0, 0, 1, hot)]));
        Assert.False(await _store.SaveTokenizedUnitAsync(picked.Id + 100, Stamp, Tokenizer, [Block(0, "abc")], []));

        var unit = (await _store.GetUnitAsync(1, 0))!;
        Assert.Equal(ReadingUnitState.Pending, unit.State);
        Assert.Empty(unit.Blocks);
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM analysis"));
    }

    [Fact]
    public async Task SaveTokenizedUnit_KeepsUnchangedSpansAndReplacesTheRest()
    {
        var (hot, cold, _) = await SeedWordsAsync();
        var kept = new AnalysisSpan(0, 0, 3, hot);
        var moved = new AnalysisSpan(0, 4, 6, cold);
        var rewritten = new AnalysisSpan(1, 0, 2, cold);
        var unitId = await TokenizedPageAsync(1, [kept, moved, rewritten]);
        await _store.RequestAnalysisAsync([1], ReadingPriority.OpenChapter);
        var before = await _store.GetUnitAnalysisAsync(unitId);
        var answer = before.Single(a => a.S == kept.S && a.Block == kept.Block);
        var outcome = await _store.StoreAnalysisAsync("gen", "p1", [new AnalysisAnswer(answer.Id, "hot:2", "hot-r1")]);
        Assert.Equal(1, outcome.Stored);

        await _store.SeedFileAsync(1, 1, NewStamp, 0);
        var added = new AnalysisSpan(0, 4, 7, cold);
        var sameStartOtherWord = new AnalysisSpan(1, 0, 2, hot);
        Assert.True(await _store.SaveTokenizedUnitAsync(unitId, NewStamp, Tokenizer, [Block(0, "x"), Block(1, "y")],
            [kept, added, sameStartOtherWord]));

        var after = await _store.GetUnitAnalysisAsync(unitId);
        Assert.Equal(3, after.Count);
        var survivor = after[0];
        Assert.Equal(answer.Id, survivor.Id);
        Assert.Equal(AnalysisState.Done, survivor.State);
        Assert.Equal("hot:2", survivor.SenseKey);
        Assert.Equal("hot-r1", survivor.Reading);
        Assert.Equal("gen", survivor.Generator);
        Assert.Equal("p1", survivor.PromptVersion);

        // The file still has its request, so new rows start pending at the request's priority
        Assert.Equal((0, 4, 7, cold), (after[1].Block, after[1].S, after[1].E, after[1].WordId));
        Assert.Equal((1, 0, 2, hot), (after[2].Block, after[2].S, after[2].E, after[2].WordId));
        Assert.All(after.Skip(1), a =>
        {
            Assert.Equal(AnalysisState.Pending, a.State);
            Assert.Equal(ReadingPriority.OpenChapter, a.Priority);
            Assert.Null(a.SenseKey);
            Assert.DoesNotContain(a.Id, before.Select(b => b.Id));
        });
    }

    [Fact]
    public async Task SaveTokenizedUnit_NewRowsAreIdleWithoutARequest()
    {
        var (hot, cold, _) = await SeedWordsAsync();

        var unitId = await TokenizedPageAsync(1, [new AnalysisSpan(0, 0, 1, hot), new AnalysisSpan(2, 3, 5, cold)]);

        var rows = await _store.GetUnitAnalysisAsync(unitId);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(AnalysisState.Idle, r.State);
            Assert.Equal(unitId, r.UnitId);
            Assert.Null(r.Generator);
        });
        Assert.Equal([0, 2], rows.Select(r => r.Block));
        Assert.Empty(await _store.GetPendingAnalysisAsync(10, 0));
    }

    [Fact]
    public async Task RecordFailedAttempt_RequeuesBelowTheLimitAndFailsAtIt()
    {
        await _store.SeedFileAsync(1, 2, Stamp, 0);
        var first = await NextAsync();
        Assert.Equal(0, first.Attempts);

        _time.Advance();
        Assert.Equal(ReadingUnitState.Pending, await _store.RecordFailedAttemptAsync(first.Id, 2));

        // Its queue time moved past page 1's, so page 1 goes first
        var second = await NextAsync();
        Assert.Equal(1, second.Page);
        Assert.True(await _store.SaveTokenizedUnitAsync(second.Id, Stamp, Tokenizer, [], []));
        var retry = await NextAsync();
        Assert.Equal(first.Id, retry.Id);
        Assert.Equal(1, retry.Attempts);

        Assert.Equal(ReadingUnitState.Failed, await _store.RecordFailedAttemptAsync(first.Id, 2));
        Assert.Equal(ReadingUnitState.Failed, (await _store.GetUnitAsync(1, 0))!.State);
        Assert.Null(await _store.GetNextPendingUnitAsync());

        // A unit that isn't pending is left as it is
        Assert.Equal(ReadingUnitState.Failed, await _store.RecordFailedAttemptAsync(first.Id, 10));
        Assert.Equal(ReadingUnitState.Done, await _store.RecordFailedAttemptAsync(second.Id, 1));
        Assert.Equal(2, await ScalarAsync($"SELECT attempts FROM unit WHERE id = {first.Id}"));
        Assert.Equal(ReadingUnitState.Failed, await _store.RecordFailedAttemptAsync(first.Id + 100, 3));
    }

    [Fact]
    public async Task DeferUnit_GoesBehindItsPriorityWithoutAnAttempt()
    {
        await _store.SeedFileAsync(1, 1, Stamp, ReadingPriority.NextChapter);
        await _store.SeedFileAsync(2, 1, Stamp, ReadingPriority.NextChapter);
        await _store.SeedFileAsync(3, 1, Stamp, ReadingPriority.WholeBook);
        var first = await NextAsync();
        Assert.Equal(1, first.MangaFileId);

        _time.Advance();
        await _store.DeferUnitAsync(first.Id);

        var order = new List<int>();
        while (await _store.GetNextPendingUnitAsync() is { } next)
        {
            order.Add(next.MangaFileId);
            if (next.Id == first.Id) Assert.Equal(0, next.Attempts);
            Assert.True(await _store.SaveTokenizedUnitAsync(next.Id, next.SourceStamp, Tokenizer, [], []));
        }

        // File 1 kept its priority, so it still goes before file 3
        Assert.Equal([2, 1, 3], order);
        Assert.Equal(ReadingPriority.NextChapter, await ScalarAsync($"SELECT priority FROM unit WHERE id = {first.Id}"));

        // A unit that isn't pending is left as it is
        await _store.DeferUnitAsync(first.Id);
        Assert.Equal(ReadingUnitState.Done, (await _store.GetUnitAsync(1, 0))!.State);
    }

    [Fact]
    public async Task RaiseUnitPriority_RaisesOnlyLowerPendingUnits()
    {
        await _store.SeedFileAsync(1, 3, Stamp, ReadingPriority.WholeBook);
        await _store.SeedFileAsync(2, 1, Stamp, ReadingPriority.OpenChapter);
        var done = (await _store.GetUnitAsync(1, 2))!;
        Assert.True(await _store.SaveTokenizedUnitAsync(done.Id, Stamp, Tokenizer, [], []));

        await _store.RaiseUnitPriorityAsync(1, 1, ReadingPriority.Tapped);
        Assert.Equal((1, 1), Page(await NextAsync()));

        await _store.RaiseUnitPriorityAsync(1, null, ReadingPriority.NextChapter);
        await _store.RaiseUnitPriorityAsync(2, null, ReadingPriority.NextChapter);

        Assert.Equal(ReadingPriority.Tapped, await PriorityAsync(1, 1));
        Assert.Equal(ReadingPriority.NextChapter, await PriorityAsync(1, 0));
        Assert.Equal(ReadingPriority.WholeBook, await PriorityAsync(1, 2));
        Assert.Equal(ReadingPriority.OpenChapter, await PriorityAsync(2, 0));
    }

    [Fact]
    public async Task RaiseUnitPriority_MovesTheQueueTimeOfARaisedUnit()
    {
        await _store.SeedFileAsync(1, 1, Stamp, ReadingPriority.WholeBook);
        _time.Advance();
        await _store.SeedFileAsync(2, 1, Stamp, ReadingPriority.NextChapter);
        _time.Advance();

        await _store.RaiseUnitPriorityAsync(1, 0, ReadingPriority.NextChapter);

        Assert.Equal((2, 0), Page(await NextAsync()));
    }

    private static (int File, int Page) Page(ReadingUnitRef unit) => (unit.MangaFileId, unit.Page);

    private async Task<long> PriorityAsync(int file, int page) =>
        await ScalarAsync($"SELECT priority FROM unit WHERE manga_file_id = {file} AND page = {page}");

    #endregion

    #region Analysis queue

    [Fact]
    public async Task RequestAnalysis_QueuesIdleRowsRaisesPendingRowsAndLeavesDoneRows()
    {
        var (hot, cold, _) = await SeedWordsAsync();
        var unitId = await TokenizedPageAsync(1,
            [new AnalysisSpan(0, 0, 1, hot), new AnalysisSpan(0, 2, 3, cold), new AnalysisSpan(0, 4, 5, hot)]);
        var other = await TokenizedPageAsync(2, [new AnalysisSpan(0, 0, 1, hot)]);

        await _store.RequestAnalysisAsync([1], ReadingPriority.WholeBook);
        var rows = await _store.GetUnitAnalysisAsync(unitId);
        Assert.All(rows, r => Assert.Equal(AnalysisState.Pending, r.State));
        await _store.StoreAnalysisAsync("gen", "p1", [new AnalysisAnswer(rows[0].Id, null, null)]);
        await _store.RaiseAnalysisPriorityAsync(1, 0, null, ReadingPriority.Tapped);

        await _store.RequestAnalysisAsync([1], ReadingPriority.OpenChapter);

        rows = await _store.GetUnitAnalysisAsync(unitId);
        Assert.Equal(AnalysisState.Done, rows[0].State);
        Assert.Equal(ReadingPriority.WholeBook, rows[0].Priority);
        Assert.All(rows.Skip(1), r =>
        {
            Assert.Equal(AnalysisState.Pending, r.State);
            Assert.Equal(ReadingPriority.Tapped, r.Priority);
        });
        Assert.Equal(AnalysisState.Idle, Assert.Single(await _store.GetUnitAnalysisAsync(other)).State);
    }

    [Fact]
    public async Task RequestAnalysis_TurnsIdleRowsPendingAtTheGivenPriority()
    {
        var (hot, _, _) = await SeedWordsAsync();
        var unitId = await TokenizedPageAsync(1, [new AnalysisSpan(0, 0, 1, hot)]);
        await _store.RequestAnalysisAsync([1], ReadingPriority.OpenChapter);
        await _store.ClearAnalysisAsync([1]);

        await _store.RequestAnalysisAsync([1, 1], ReadingPriority.NextChapter);

        var row = Assert.Single(await _store.GetUnitAnalysisAsync(unitId));
        Assert.Equal(AnalysisState.Pending, row.State);
        Assert.Equal(ReadingPriority.NextChapter, row.Priority);
    }

    [Fact]
    public async Task RequestAnalysis_RaisesRowsThatAreAlreadyPending()
    {
        var (hot, cold, _) = await SeedWordsAsync();
        var unitId = await TokenizedPageAsync(1, [new AnalysisSpan(0, 0, 1, hot), new AnalysisSpan(0, 2, 3, cold)]);
        await _store.RequestAnalysisAsync([1], ReadingPriority.WholeBook);

        await _store.RequestAnalysisAsync([1], ReadingPriority.OpenChapter);

        Assert.All(await _store.GetUnitAnalysisAsync(unitId), r =>
        {
            Assert.Equal(AnalysisState.Pending, r.State);
            Assert.Equal(ReadingPriority.OpenChapter, r.Priority);
        });
    }

    [Fact]
    public async Task RequestAnalysis_KeepsTheHigherRequestPriorityForRowsAddedLater()
    {
        var (hot, cold, _) = await SeedWordsAsync();
        var unitId = await TokenizedPageAsync(1, [new AnalysisSpan(0, 0, 1, hot)]);
        await _store.RequestAnalysisAsync([1], ReadingPriority.OpenChapter);
        await _store.RequestAnalysisAsync([1], ReadingPriority.WholeBook);
        Assert.Equal(ReadingPriority.OpenChapter, await ScalarAsync("SELECT priority FROM analysis_request"));

        await _store.SeedFileAsync(1, 1, NewStamp, 0);
        Assert.True(await _store.SaveTokenizedUnitAsync(unitId, NewStamp, Tokenizer, [Block(0, "x")],
            [new AnalysisSpan(0, 0, 1, hot), new AnalysisSpan(0, 2, 3, cold)]));

        var added = (await _store.GetUnitAnalysisAsync(unitId))[1];
        Assert.Equal(AnalysisState.Pending, added.State);
        Assert.Equal(ReadingPriority.OpenChapter, added.Priority);
    }

    [Fact]
    public async Task RaiseAnalysisPriority_NeverQueuesIdleRows()
    {
        var (hot, _, _) = await SeedWordsAsync();
        var unitId = await TokenizedPageAsync(1, [new AnalysisSpan(0, 0, 1, hot)]);

        await _store.RaiseAnalysisPriorityAsync(1, 0, 0, ReadingPriority.Tapped);
        await _store.RaiseAnalysisPriorityAsync(1, null, null, ReadingPriority.Tapped);

        var row = Assert.Single(await _store.GetUnitAnalysisAsync(unitId));
        Assert.Equal(AnalysisState.Idle, row.State);
        Assert.Equal(ReadingPriority.WholeBook, row.Priority);
        Assert.Empty(await _store.GetPendingAnalysisAsync(10, 0));
    }

    [Fact]
    public async Task RaiseAnalysisPriority_ScopesToTheFilePageAndBlock()
    {
        var (hot, _, _) = await SeedWordsAsync();
        await _store.SeedFileAsync(1, 2, Stamp, 0);
        var page0 = (await _store.GetUnitAsync(1, 0))!.Id;
        var page1 = (await _store.GetUnitAsync(1, 1))!.Id;
        AnalysisSpan[] spans = [new(0, 0, 1, hot), new(1, 0, 1, hot)];
        Assert.True(await _store.SaveTokenizedUnitAsync(page0, Stamp, Tokenizer, [Block(0, "a"), Block(1, "b")], spans));
        Assert.True(await _store.SaveTokenizedUnitAsync(page1, Stamp, Tokenizer, [Block(0, "a"), Block(1, "b")], spans));
        var other = await TokenizedPageAsync(2, [new AnalysisSpan(1, 0, 1, hot)]);
        await _store.RequestAnalysisAsync([1, 2], ReadingPriority.WholeBook);

        await _store.RaiseAnalysisPriorityAsync(1, 0, 1, ReadingPriority.Tapped);
        Assert.Equal([0, 30], (await _store.GetUnitAnalysisAsync(page0)).Select(a => a.Priority));
        Assert.Equal([0, 0], (await _store.GetUnitAnalysisAsync(page1)).Select(a => a.Priority));

        await _store.RaiseAnalysisPriorityAsync(1, 0, null, ReadingPriority.OpenChapter);
        Assert.Equal([20, 30], (await _store.GetUnitAnalysisAsync(page0)).Select(a => a.Priority));

        // The block is ignored without a page, so this raises every pending row of the file
        await _store.RaiseAnalysisPriorityAsync(1, null, 1, ReadingPriority.NextChapter);
        Assert.Equal([20, 30], (await _store.GetUnitAnalysisAsync(page0)).Select(a => a.Priority));
        Assert.Equal([10, 10], (await _store.GetUnitAnalysisAsync(page1)).Select(a => a.Priority));
        Assert.Equal(0, Assert.Single(await _store.GetUnitAnalysisAsync(other)).Priority);
    }

    [Fact]
    public async Task GetPendingAnalysis_ReturnsQueueOrderWithLimitAndMinPriority()
    {
        var (hot, cold, _) = await SeedWordsAsync();
        var first = await TokenizedPageAsync(1, [new AnalysisSpan(0, 0, 1, hot), new AnalysisSpan(1, 2, 4, cold)],
            text: "first");
        await _store.RequestAnalysisAsync([1], ReadingPriority.WholeBook);
        _time.Advance();
        await TokenizedPageAsync(2, [new AnalysisSpan(0, 0, 1, hot)], text: "second");
        await _store.RequestAnalysisAsync([2], ReadingPriority.WholeBook);
        _time.Advance();
        await _store.RaiseAnalysisPriorityAsync(1, 0, 1, ReadingPriority.Tapped);

        var all = await _store.GetPendingAnalysisAsync(10, 0);

        Assert.Equal([(1, 1), (1, 0), (2, 0)], all.Select(r => (r.MangaFileId, r.Block)));
        var top = all[0];
        Assert.Equal((first, 0, 2, 4, cold, 30, "first-1"),
            (top.UnitId, top.Page, top.S, top.E, top.WordId, top.Priority, top.BlockText));
        Assert.Equal("second-0", all[2].BlockText);

        Assert.Equal(all.Take(2).Select(r => r.Id), (await _store.GetPendingAnalysisAsync(2, 0)).Select(r => r.Id));
        Assert.Equal([top.Id], (await _store.GetPendingAnalysisAsync(10, ReadingPriority.Tapped)).Select(r => r.Id));
        Assert.Empty(await _store.GetPendingAnalysisAsync(10, ReadingPriority.Tapped + 1));
        Assert.Empty(await _store.GetPendingAnalysisAsync(0, 0));
    }

    [Fact]
    public async Task GetPendingAnalysis_BreaksTiesById()
    {
        var (hot, cold, _) = await SeedWordsAsync();
        await TokenizedPageAsync(1, [new AnalysisSpan(0, 5, 6, hot), new AnalysisSpan(0, 0, 1, cold)]);
        await _store.RequestAnalysisAsync([1], 0);

        var rows = await _store.GetPendingAnalysisAsync(10, 0);

        Assert.Equal(rows.Select(r => r.Id).Order(), rows.Select(r => r.Id));
    }

    #endregion

    #region Storing answers

    [Fact]
    public async Task StoreAnalysis_SavesValidAnswersAndRejectsEachBadOneWithItsReason()
    {
        var (hot, cold, _) = await SeedWordsAsync();
        var unitId = await TokenizedPageAsync(1,
        [
            new AnalysisSpan(0, 0, 1, hot), new AnalysisSpan(0, 1, 2, hot), new AnalysisSpan(0, 2, 3, cold),
            new AnalysisSpan(0, 3, 4, cold), new AnalysisSpan(0, 4, 5, hot), new AnalysisSpan(0, 5, 6, hot),
        ]);
        var idle = await TokenizedPageAsync(2, [new AnalysisSpan(0, 0, 1, hot)]);
        await _store.RequestAnalysisAsync([1], 0);
        var ids = (await _store.GetUnitAnalysisAsync(unitId)).Select(a => a.Id).ToArray();
        var idleId = Assert.Single(await _store.GetUnitAnalysisAsync(idle)).Id;

        var outcome = await _store.StoreAnalysisAsync("claude-sonnet", "v2",
        [
            new AnalysisAnswer(ids[0], "hot:2", "hot-r1"),
            new AnalysisAnswer(ids[1], "cold:1", null),
            new AnalysisAnswer(ids[2], "cold:3", "cold-r9"),
            new AnalysisAnswer(ids[3], null, null),
            new AnalysisAnswer(ids[4], null, "hot-r0"),
            new AnalysisAnswer(ids[0], "hot:1", null),
            new AnalysisAnswer(idleId, null, null),
            new AnalysisAnswer(ids[5] + 1000, null, null),
        ]);

        Assert.Equal(3, outcome.Stored);
        Assert.Equal(
        [
            new AnalysisRejection(ids[1], AnalysisRejectReason.SenseNotOffered),
            new AnalysisRejection(ids[2], AnalysisRejectReason.ReadingNotOffered),
            new AnalysisRejection(ids[0], AnalysisRejectReason.Duplicate),
            new AnalysisRejection(idleId, AnalysisRejectReason.NotPending),
            new AnalysisRejection(ids[5] + 1000, AnalysisRejectReason.NotPending),
        ], outcome.Rejected);

        var rows = await _store.GetUnitAnalysisAsync(unitId);
        Assert.Equal(
        [
            (AnalysisState.Done, "hot:2", "hot-r1"),
            (AnalysisState.Pending, null, null),
            (AnalysisState.Pending, null, null),
            (AnalysisState.Done, null, null),
            (AnalysisState.Done, null, "hot-r0"),
            (AnalysisState.Pending, null, null),
        ], rows.Select(r => (r.State, r.SenseKey, r.Reading)));
        Assert.All(rows.Where(r => r.State == AnalysisState.Done), r =>
        {
            Assert.Equal("claude-sonnet", r.Generator);
            Assert.Equal("v2", r.PromptVersion);
        });
        Assert.Equal(3, (await _store.GetPendingAnalysisAsync(10, 0)).Count);
    }

    [Fact]
    public async Task StoreAnalysis_ARejectedAnswerDoesNotBlockALaterOneForTheSameId()
    {
        var (hot, _, _) = await SeedWordsAsync();
        var unitId = await TokenizedPageAsync(1, [new AnalysisSpan(0, 0, 1, hot)]);
        await _store.RequestAnalysisAsync([1], 0);
        var id = Assert.Single(await _store.GetUnitAnalysisAsync(unitId)).Id;

        var outcome = await _store.StoreAnalysisAsync("gen", "p1",
        [
            new AnalysisAnswer(id, "nope:1", null),
            new AnalysisAnswer(id, "hot:2", null),
            new AnalysisAnswer(id, "hot:1", null),
        ]);

        Assert.Equal(1, outcome.Stored);
        Assert.Equal(
        [
            new AnalysisRejection(id, AnalysisRejectReason.SenseNotOffered),
            new AnalysisRejection(id, AnalysisRejectReason.Duplicate),
        ], outcome.Rejected);
        Assert.Equal("hot:2", Assert.Single(await _store.GetUnitAnalysisAsync(unitId)).SenseKey);
    }

    [Fact]
    public async Task StoreAnalysis_ListsEachPageThatGainedAnAnswerOnceInFileThenPageOrder()
    {
        var (hot, _, _) = await SeedWordsAsync();
        AnalysisSpan[] spans = [new(0, 0, 1, hot), new(0, 2, 3, hot)];
        await _store.SeedFileAsync(5, 3, Stamp, 0);
        await _store.SeedFileAsync(2, 1, Stamp, 0);
        var units = new Dictionary<(int File, int Page), long>();
        foreach (var (file, page) in new[] { (5, 2), (5, 0), (5, 1), (2, 0) })
        {
            var unit = (await _store.GetUnitAsync(file, page))!;
            Assert.True(await _store.SaveTokenizedUnitAsync(unit.Id, Stamp, Tokenizer, [Block(0, "abc")], spans));
            units[(file, page)] = unit.Id;
        }
        await _store.RequestAnalysisAsync([5, 2], 0);
        async Task<long[]> Ids((int, int) key) =>
            (await _store.GetUnitAnalysisAsync(units[key])).Select(a => a.Id).ToArray();
        var p52 = await Ids((5, 2));
        var p50 = await Ids((5, 0));
        var p51 = await Ids((5, 1));
        var p20 = await Ids((2, 0));

        var outcome = await _store.StoreAnalysisAsync("gen", "p1",
        [
            new AnalysisAnswer(p52[0], null, null),
            new AnalysisAnswer(p20[1], null, null),
            new AnalysisAnswer(p52[1], null, null),
            new AnalysisAnswer(p50[0], null, null),
            new AnalysisAnswer(p51[0], "bad", null),
        ]);

        Assert.Equal(4, outcome.Stored);
        Assert.Equal([new AnalysisStoredPage(2, 0), new AnalysisStoredPage(5, 0), new AnalysisStoredPage(5, 2)],
            outcome.StoredPages);

        var none = await _store.StoreAnalysisAsync("gen", "p1", [new AnalysisAnswer(p52[0], null, null)]);
        Assert.Empty(none.StoredPages);
        Assert.Empty((await _store.StoreAnalysisAsync("gen", "p1", [])).StoredPages);
    }

    [Fact]
    public async Task StoreAnalysis_RejectsARowThatIsAlreadyDone()
    {
        var (hot, _, _) = await SeedWordsAsync();
        var unitId = await TokenizedPageAsync(1, [new AnalysisSpan(0, 0, 1, hot)]);
        await _store.RequestAnalysisAsync([1], 0);
        var id = Assert.Single(await _store.GetUnitAnalysisAsync(unitId)).Id;
        Assert.Equal(1, (await _store.StoreAnalysisAsync("gen", "p1", [new AnalysisAnswer(id, "hot:1", null)])).Stored);

        var outcome = await _store.StoreAnalysisAsync("gen", "p2", [new AnalysisAnswer(id, "hot:2", null)]);

        Assert.Equal(0, outcome.Stored);
        Assert.Equal([new AnalysisRejection(id, AnalysisRejectReason.NotPending)], outcome.Rejected);
        var row = Assert.Single(await _store.GetUnitAnalysisAsync(unitId));
        Assert.Equal(("hot:1", "p1"), (row.SenseKey, row.PromptVersion));
        Assert.Empty((await _store.StoreAnalysisAsync("gen", "p1", [])).Rejected);
    }

    [Fact]
    public async Task ClearAnalysis_SendsRowsBackToIdleAndDropsTheRequest()
    {
        var (hot, cold, _) = await SeedWordsAsync();
        var unitId = await TokenizedPageAsync(1, [new AnalysisSpan(0, 0, 1, hot), new AnalysisSpan(0, 2, 3, hot)]);
        var other = await TokenizedPageAsync(2, [new AnalysisSpan(0, 0, 1, hot)]);
        await _store.RequestAnalysisAsync([1, 2], ReadingPriority.OpenChapter);
        var ids = (await _store.GetUnitAnalysisAsync(unitId)).Select(a => a.Id).ToArray();
        await _store.StoreAnalysisAsync("gen", "p1", [new AnalysisAnswer(ids[0], "hot:1", "hot-r1")]);

        await _store.ClearAnalysisAsync([1]);

        Assert.All(await _store.GetUnitAnalysisAsync(unitId), r =>
        {
            Assert.Equal(AnalysisState.Idle, r.State);
            Assert.Equal(ReadingPriority.WholeBook, r.Priority);
            Assert.Null(r.SenseKey);
            Assert.Null(r.Reading);
            Assert.Null(r.Generator);
            Assert.Null(r.PromptVersion);
        });
        Assert.Equal(AnalysisState.Pending, Assert.Single(await _store.GetUnitAnalysisAsync(other)).State);
        Assert.Equal(2, await ScalarAsync("SELECT manga_file_id FROM analysis_request"));

        // Without a request, rows added by a later tokenize start idle
        await _store.SeedFileAsync(1, 1, NewStamp, 0);
        Assert.True(await _store.SaveTokenizedUnitAsync(unitId, NewStamp, Tokenizer, [Block(0, "x")],
            [new AnalysisSpan(0, 4, 5, cold)]));
        Assert.Equal(AnalysisState.Idle, Assert.Single(await _store.GetUnitAnalysisAsync(unitId)).State);
    }

    #endregion

    #region Counts

    [Fact]
    public async Task GetCounts_CountsByStateForTheWholeStoreOrSomeFiles()
    {
        Assert.Equal(new ReadingStoreCounts(0, 0, 0, 0, 0, 0), await _store.GetCountsAsync(null));

        var (hot, _, _) = await SeedWordsAsync();
        var unitId = await TokenizedPageAsync(1, [new AnalysisSpan(0, 0, 1, hot), new AnalysisSpan(0, 2, 3, hot)],
            pageCount: 3);
        await _store.RecordFailedAttemptAsync((await NextAsync()).Id, 1);
        await TokenizedPageAsync(2, [new AnalysisSpan(0, 0, 1, hot)]);
        await _store.RequestAnalysisAsync([1], 0);
        var id = (await _store.GetUnitAnalysisAsync(unitId))[0].Id;
        await _store.StoreAnalysisAsync("gen", "p1", [new AnalysisAnswer(id, null, null)]);

        Assert.Equal(new ReadingStoreCounts(1, 2, 1, 1, 1, 1), await _store.GetCountsAsync(null));
        Assert.Equal(new ReadingStoreCounts(1, 1, 1, 0, 1, 1), await _store.GetCountsAsync([1]));
        Assert.Equal(new ReadingStoreCounts(0, 1, 0, 1, 0, 0), await _store.GetCountsAsync([2, 2]));
        Assert.Equal(new ReadingStoreCounts(0, 0, 0, 0, 0, 0), await _store.GetCountsAsync([]));
        Assert.Equal(await _store.GetCountsAsync(null),
            await _store.GetCountsAsync(Enumerable.Range(1, 1200).ToList()));
    }

    #endregion

    #region Concurrency

    [Fact]
    public async Task ConcurrentWriters_WaitForEachOtherInsteadOfFailing()
    {
        var words = await SeedWordsAsync();

        await Task.WhenAll(Enumerable.Range(1, 20).Select(file => Task.Run(async () =>
        {
            await _store.SeedFileAsync(file, 5, Stamp, 0);
            await _store.UpsertWordsAsync([Word("shared"), Word($"own{file}")], Lexicon);
            var unit = (await _store.GetUnitAsync(file, 0))!;
            await _store.SaveTokenizedUnitAsync(unit.Id, Stamp, Tokenizer, [Block(0, "x")],
                [new AnalysisSpan(0, 0, 1, words.Hot)]);
            await _store.RequestAnalysisAsync([file], 0);
        })));

        Assert.Equal(new ReadingStoreCounts(80, 20, 0, 0, 20, 0), await _store.GetCountsAsync(null));
        Assert.Equal(24, await ScalarAsync("SELECT COUNT(*) FROM word"));
    }

    #endregion
}
