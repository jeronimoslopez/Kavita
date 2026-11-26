using System.Text.Json;
using Kavita.API.Services;
using Kavita.Models.DTOs.Dictionary;
using Kavita.Services.Dictionary;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace Kavita.Services.Tests.Dictionary;

public class ReadingStoreTests : IDisposable
{
    private const string Stamp = "stamp-1";
    private const string NewStamp = "stamp-2";
    private const string Lexicon = "lex-1";
    private const string Tokenizer = "sidecar=1";

    private readonly ITestOutputHelper _output;
    private readonly string _directory;
    private readonly ReadingDatabase _database;
    private readonly ManualTime _time = new();
    private readonly ReadingStore _store;

    public ReadingStoreTests(ITestOutputHelper output)
    {
        _output = output;
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
    /// Where a test wants an analysis row, given as a top-level token over a word
    /// </summary>
    private sealed record AnalysisSpan(int Block, int S, int E, long WordId);

    /// <summary>
    /// Saves the blocks with one top-level token per span added to them, so the store finds those spans when the
    /// span's word is ambiguous
    /// </summary>
    private Task<bool> SaveAsync(long unitId, string stamp, IReadOnlyList<StoredBlock> blocks,
        IReadOnlyList<AnalysisSpan> spans)
    {
        var withTokens = blocks.Select(b => b.Index).Concat(spans.Select(s => s.Block)).Distinct().Order()
            .Select(index =>
            {
                var block = blocks.FirstOrDefault(b => b.Index == index) ?? Block(index, $"block-{index}");
                var tokens = block.Tokens
                    .Concat(spans.Where(s => s.Block == index).Select(s => Token(s.S, s.E, s.WordId)))
                    .OrderBy(t => t.S)
                    .ToList();
                return block with { Tokens = tokens };
            })
            .ToList();
        return _store.SaveTokenizedUnitAsync(unitId, stamp, Tokenizer, withTokens);
    }

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
        Assert.True(await SaveAsync(unit.Id, Stamp, blocks, spans));
        return unit.Id;
    }

    private async Task<SqliteConnection> OpenAsync() => await _database.OpenConnectionAsync();

    private async Task<string> TextAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string) (await command.ExecuteScalarAsync())!;
    }

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

    private static readonly SidecarWordDto Today = new()
    {
        Lemma = "今日", Pos = "名詞", Reading = "きょう", Jlpt = 5, Readings = ["きょう", "こんにち"],
        Senses = [new SenseDto("1579110:1", ["n"], ["today"]), new SenseDto("1579110:2", ["n"], ["these days"], ["こんにち"])],
        Entries = [new WordEntryDto(1579110, true, ["きょう", "こんにち"])],
    };

    [Fact]
    public async Task GetWords_RoundTripsEntriesAndRestrictedSenses()
    {
        var id = (await _store.UpsertWordsAsync([Today], Lexicon))[0];

        var word = (await _store.GetWordsAsync([id]))[id];

        var entry = Assert.Single(word.Entries!);
        Assert.Equal((1579110L, true), (entry.Seq, entry.Common));
        Assert.Equal(["きょう", "こんにち"], entry.Readings);
        Assert.Null(word.Senses[0].R);
        Assert.Equal(["こんにち"], word.Senses[1].R!);
        // Senses without a restriction are written without the key, as they were before restrictions
        Assert.Equal("""[{"key":"1579110:1","pos":["n"],"gloss":["today"]},{"key":"1579110:2","pos":["n"],"gloss":["these days"],"r":["こんにち"]}]""",
            await TextAsync($"SELECT senses FROM word WHERE id = {id}"));
        Assert.Equal("""[{"seq":1579110,"common":true,"readings":["きょう","こんにち"]}]""",
            await TextAsync($"SELECT entries FROM word WHERE id = {id}"));
    }

    [Fact]
    public async Task GetWords_TellsAWordWithoutTheFieldFromOneWithEmptyEntries()
    {
        var ids = await _store.UpsertWordsAsync([Word("a"), Word("b") with { Entries = [] }], Lexicon);

        var words = await _store.GetWordsAsync(ids);

        Assert.Null(words[ids[0]].Entries);
        Assert.NotNull(words[ids[1]].Entries);
        Assert.Empty(words[ids[1]].Entries!);
        Assert.Equal("[]", await TextAsync($"SELECT entries FROM word WHERE id = {ids[1]}"));
    }

    [Fact]
    public async Task UpsertWords_AVersion2WordGetsItsEntriesWhenAVersion3AnswerWritesItAgain()
    {
        var version2 = Today with { Entries = null, Senses = Today.Senses.Select(s => s with { R = null }).ToList() };
        var id = (await _store.UpsertWordsAsync([version2], "lex-2"))[0];
        Assert.Null((await _store.GetWordsAsync([id]))[id].Entries);
        Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM word WHERE id = {id} AND entries IS NULL"));

        var again = await _store.UpsertWordsAsync([Today], "lex-3");

        Assert.Equal(id, again[0]);
        var word = (await _store.GetWordsAsync([id]))[id];
        Assert.Equal([1579110L], word.Entries!.Select(e => e.Seq));
        Assert.Equal(["こんにち"], word.Senses[1].R!);
        Assert.Equal("lex-3", word.LexiconVersion);
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
        await _store.RequestAnalysisAsync([1], 0);
        var last = (await _store.GetUnitAsync(1, 2))!;
        Assert.True(await SaveAsync(last.Id, Stamp, [Block(0, "abc")],
            [new AnalysisSpan(0, 0, 1, hot)]));
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM analysis"));

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
        await _store.RequestAnalysisAsync([1], 0);
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
            Assert.True(await SaveAsync(next.Id, next.SourceStamp, [], []));
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

        Assert.True(await SaveAsync(next.Id, Stamp, blocks, []));

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

        Assert.False(await SaveAsync(picked.Id, picked.SourceStamp,
            [Block(0, "abc")], [new AnalysisSpan(0, 0, 1, hot)]));
        Assert.False(await SaveAsync(picked.Id + 100, Stamp, [Block(0, "abc")], []));

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
        Assert.True(await SaveAsync(unitId, NewStamp, [Block(0, "x"), Block(1, "y")],
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
    public async Task SaveTokenizedUnit_WithoutARequest_WritesNoRowsAndDeletesOldOnes()
    {
        var (hot, cold, _) = await SeedWordsAsync();

        var unitId = await TokenizedPageAsync(1, [new AnalysisSpan(0, 0, 1, hot), new AnalysisSpan(2, 3, 5, cold)]);

        Assert.Empty(await _store.GetUnitAnalysisAsync(unitId));
        Assert.Empty(await _store.GetPendingAnalysisAsync(10, 0));

        // Rows left behind by a request that's gone, as an older store kept them
        await _store.RequestAnalysisAsync([1], 0);
        Assert.Equal(2, (await _store.GetUnitAnalysisAsync(unitId)).Count);
        await ScalarAsync("DELETE FROM analysis_request");

        await _store.SeedFileAsync(1, 1, NewStamp, 0);
        Assert.True(await SaveAsync(unitId, NewStamp, [Block(0, "x")], [new AnalysisSpan(0, 0, 1, hot)]));

        Assert.Empty(await _store.GetUnitAnalysisAsync(unitId));
    }

    [Fact]
    public async Task SaveTokenizedUnit_WithARequest_AddsPendingRowsForAmbiguousTopLevelTokensOnly()
    {
        var (hot, cold, plain) = await SeedWordsAsync();
        await _store.SeedFileAsync(1, 1, Stamp, 0);
        await _store.RequestAnalysisAsync([1], ReadingPriority.NextChapter);
        var unit = (await _store.GetUnitAsync(1, 0))!;

        // The compound is plain, and its ambiguous part never gets a row of its own
        StoredBlock[] blocks =
        [
            Block(0, "abcdefgh", Token(0, 2, hot), Token(2, 3, plain), Token(3, 4, null),
                Token(4, 6, plain, sub: [new SubTokenDto(4, 5, hot, null), new SubTokenDto(5, 6, cold, null)])),
            Block(1, "xyz", Token(0, 3, cold)),
        ];
        Assert.True(await _store.SaveTokenizedUnitAsync(unit.Id, Stamp, Tokenizer, blocks));

        var rows = await _store.GetUnitAnalysisAsync(unit.Id);
        Assert.Equal([(0, 0, 2, hot), (1, 0, 3, cold)], rows.Select(r => (r.Block, r.S, r.E, r.WordId)));
        Assert.All(rows, r =>
        {
            Assert.Equal(AnalysisState.Pending, r.State);
            Assert.Equal(ReadingPriority.NextChapter, r.Priority);
            Assert.Null(r.Generator);
        });
    }

    [Fact]
    public async Task SaveTokenizedUnit_APhraseGetsNoRowEvenWhenItsWordIsAmbiguous()
    {
        var (hot, cold, plain) = await SeedWordsAsync();
        await _store.SeedFileAsync(1, 1, Stamp, 0);
        await _store.RequestAnalysisAsync([1], ReadingPriority.NextChapter);
        var unit = (await _store.GetUnitAsync(1, 0))!;

        StoredBlock[] blocks =
        [
            Block(0, "abcd", Token(0, 1, plain), Token(1, 2, null), Token(2, 3, cold), Token(3, 4, null))
                with { Phrases = [new PhraseDto(0, 3, hot)] },
        ];
        Assert.True(await _store.SaveTokenizedUnitAsync(unit.Id, Stamp, Tokenizer, blocks));

        Assert.Equal([(0, 2, 3, cold)], (await _store.GetUnitAnalysisAsync(unit.Id)).Select(r => (r.Block, r.S, r.E, r.WordId)));
        Assert.Equal([new PhraseDto(0, 3, hot)], (await _store.GetUnitAsync(1, 0))!.Blocks[0].Phrases);

        // A request made after the save builds its rows from the stored blocks, and passes over the phrase the same way
        await _store.ClearAnalysisAsync([1]);
        await _store.RequestAnalysisAsync([1], ReadingPriority.NextChapter);
        Assert.Equal([cold], (await _store.GetUnitAnalysisAsync(unit.Id)).Select(r => r.WordId));
    }

    [Fact]
    public async Task AmbiguousWords_MatchSidecarWordDtoIsAmbiguous()
    {
        var sample = JsonSerializer.Deserialize<TokenizeResponseDto>(File.ReadAllText(
            Path.Join(Directory.GetCurrentDirectory(), "../../../../text-sidecar-contract/sample-response.json")))!;
        List<SidecarWordDto> words =
        [
            ..sample.Words,
            Word("one-one"), Word("two-senses", senses: 2), Word("two-readings", readings: 2),
            Word("no-senses", senses: 0), Word("no-senses-two-readings", senses: 0, readings: 2),
            Word("no-readings-two-senses", senses: 2, readings: 0),
        ];
        Assert.Contains(words, w => w.IsAmbiguous);
        Assert.Contains(words, w => !w.IsAmbiguous);
        var ids = await _store.UpsertWordsAsync(words, Lexicon);
        await _store.SeedFileAsync(1, 1, Stamp, 0);
        await _store.RequestAnalysisAsync([1], 0);
        var unit = (await _store.GetUnitAsync(1, 0))!;

        var tokens = ids.Select((id, i) => Token(i, i + 1, id)).ToArray();
        Assert.True(await _store.SaveTokenizedUnitAsync(unit.Id, Stamp, Tokenizer, [Block(0, new string('x', ids.Count), tokens)]));

        var expected = ids.Where((_, i) => words[i].IsAmbiguous).ToList();
        Assert.Equal(expected, (await _store.GetUnitAnalysisAsync(unit.Id)).Select(r => r.WordId));
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
        Assert.True(await SaveAsync(second.Id, Stamp, [], []));
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

    #region Refresh

    private const string NewTokenizer = "sidecar=2";

    /// <summary>
    /// Seeds the file's pages and saves each with one token, by <see cref="Tokenizer"/>
    /// </summary>
    private async Task TokenizeFileAsync(int file, int pageCount, long wordId)
    {
        await _store.SeedFileAsync(file, pageCount, Stamp, ReadingPriority.WholeBook);
        for (var page = 0; page < pageCount; page++)
        {
            var unit = (await _store.GetUnitAsync(file, page))!;
            Assert.True(await _store.SaveTokenizedUnitAsync(unit.Id, Stamp, Tokenizer,
                [Block(0, $"old-{page}", Token(0, 3, wordId))]));
        }
    }

    [Fact]
    public async Task RefreshUnits_TheSameVersionQueuesNothing()
    {
        var (_, _, plain) = await SeedWordsAsync();
        await TokenizeFileAsync(1, 2, plain);

        Assert.Equal(0, await _store.RefreshUnitsAsync(1, Tokenizer, ReadingPriority.OpenChapter));

        Assert.Null(await _store.GetNextPendingUnitAsync());
        Assert.Equal(ReadingUnitState.Done, (await _store.GetUnitAsync(1, 0))!.State);
    }

    [Fact]
    public async Task RefreshUnits_AnotherVersionQueuesOnlyThatFilesTokenizedUnits()
    {
        var (_, _, plain) = await SeedWordsAsync();
        await TokenizeFileAsync(1, 3, plain);
        await TokenizeFileAsync(2, 1, plain);
        // Page 2 of file 1 had no text, so it was saved with no blocks and no version
        var empty = (await _store.GetUnitAsync(1, 2))!;
        Assert.True(await _store.SaveTokenizedUnitAsync(empty.Id, Stamp, string.Empty, []));
        await _store.SeedFileAsync(3, 1, Stamp, ReadingPriority.WholeBook);
        _time.Advance();

        Assert.Equal(2, await _store.RefreshUnitsAsync(1, NewTokenizer, ReadingPriority.OpenChapter));
        Assert.Equal(0, await _store.RefreshUnitsAsync(3, NewTokenizer, ReadingPriority.OpenChapter));

        Assert.Equal([ReadingUnitState.Refresh, ReadingUnitState.Refresh, ReadingUnitState.Done],
            await Task.WhenAll(Enumerable.Range(0, 3).Select(async p => (await _store.GetUnitAsync(1, p))!.State)));
        Assert.Equal(ReadingUnitState.Done, (await _store.GetUnitAsync(2, 0))!.State);
        Assert.Equal(ReadingUnitState.Pending, (await _store.GetUnitAsync(3, 0))!.State);
        Assert.Equal(2, await ScalarAsync($"SELECT COUNT(*) FROM unit WHERE state = 'refresh' AND priority = {ReadingPriority.OpenChapter}"));
        Assert.Equal(new ReadingStoreCounts(3, 2, 0, 0, 0), await _store.GetCountsAsync(null));
    }

    [Fact]
    public async Task RefreshUnit_ServesItsOldBlocksUntilTheNewOnesReplaceThemWithPhrases()
    {
        var (hot, cold, plain) = await SeedWordsAsync();
        await TokenizeFileAsync(1, 1, plain);
        await _store.RequestAnalysisAsync([1], ReadingPriority.WholeBook);
        await _store.RefreshUnitsAsync(1, NewTokenizer, ReadingPriority.OpenChapter);

        var waiting = (await _store.GetUnitAsync(1, 0))!;
        Assert.Equal(ReadingUnitState.Refresh, waiting.State);
        Assert.Equal(Tokenizer, waiting.TokenizerVersion);
        Assert.Equal("old-0", Assert.Single(waiting.Blocks).Text);

        var picked = await NextAsync();
        Assert.Equal(waiting.Id, picked.Id);
        StoredBlock[] fresh =
        [
            Block(0, "new", Token(0, 1, cold), Token(1, 3, plain)) with { Phrases = [new PhraseDto(0, 3, hot)] },
        ];
        Assert.True(await _store.SaveTokenizedUnitAsync(picked.Id, picked.SourceStamp, NewTokenizer, fresh));

        var saved = (await _store.GetUnitAsync(1, 0))!;
        Assert.Equal(ReadingUnitState.Done, saved.State);
        Assert.Equal(NewTokenizer, saved.TokenizerVersion);
        Assert.Equal("new", saved.Blocks[0].Text);
        Assert.Equal([new PhraseDto(0, 3, hot)], saved.Blocks[0].Phrases);
        // The ambiguous token gets its row, and the phrase over it doesn't
        Assert.Equal([cold], (await _store.GetUnitAnalysisAsync(saved.Id)).Select(r => r.WordId));
        Assert.Null(await _store.GetNextPendingUnitAsync());
    }

    [Fact]
    public async Task GetNextPendingUnit_TakesRefreshAndPendingUnitsInOneQueueOrder()
    {
        var (_, _, plain) = await SeedWordsAsync();
        await TokenizeFileAsync(1, 1, plain);
        _time.Advance();
        await _store.SeedFileAsync(2, 1, Stamp, ReadingPriority.OpenChapter);
        _time.Advance();
        await _store.SeedFileAsync(3, 1, Stamp, ReadingPriority.WholeBook);
        _time.Advance();
        await _store.RefreshUnitsAsync(1, NewTokenizer, ReadingPriority.OpenChapter);

        var order = new List<int>();
        while (await _store.GetNextPendingUnitAsync() is { } next)
        {
            order.Add(next.MangaFileId);
            Assert.True(await _store.SaveTokenizedUnitAsync(next.Id, next.SourceStamp, NewTokenizer, []));
        }

        // File 2 was queued at the same priority before file 1's refresh, and file 3 is below both
        Assert.Equal([2, 1, 3], order);
    }

    [Fact]
    public async Task RefreshUnit_ThatKeepsFailingGoesBackToDoneWithItsBlocks()
    {
        var (_, _, plain) = await SeedWordsAsync();
        await TokenizeFileAsync(1, 1, plain);
        await _store.RefreshUnitsAsync(1, NewTokenizer, ReadingPriority.OpenChapter);
        var unit = await NextAsync();

        Assert.Equal(ReadingUnitState.Refresh, await _store.RecordFailedAttemptAsync(unit.Id, 2));
        Assert.Equal(ReadingUnitState.Done, await _store.RecordFailedAttemptAsync(unit.Id, 2));

        var after = (await _store.GetUnitAsync(1, 0))!;
        Assert.Equal(Tokenizer, after.TokenizerVersion);
        Assert.Equal("old-0", Assert.Single(after.Blocks).Text);
        Assert.Equal(0, await ScalarAsync($"SELECT attempts FROM unit WHERE id = {unit.Id}"));
        Assert.Null(await _store.GetNextPendingUnitAsync());
        // A later chapter open tries again
        Assert.Equal(1, await _store.RefreshUnitsAsync(1, NewTokenizer, ReadingPriority.OpenChapter));
    }

    [Fact]
    public async Task RefreshUnit_WhoseFileChangesGoesBackToPending()
    {
        var (_, _, plain) = await SeedWordsAsync();
        await TokenizeFileAsync(1, 1, plain);
        await _store.RefreshUnitsAsync(1, NewTokenizer, ReadingPriority.OpenChapter);

        await _store.SeedFileAsync(1, 1, NewStamp, ReadingPriority.WholeBook);

        Assert.Equal(ReadingUnitState.Pending, (await _store.GetUnitAsync(1, 0))!.State);
    }

    #endregion

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
            Assert.True(await SaveAsync(next.Id, next.SourceStamp, [], []));
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
        Assert.True(await SaveAsync(done.Id, Stamp, [], []));

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
    public async Task RequestAnalysis_CreatesRowsForTokenizedUnitsOfThatFileOnly()
    {
        var (hot, cold, plain) = await SeedWordsAsync();
        var unitId = await TokenizedPageAsync(1,
            [new AnalysisSpan(0, 0, 1, hot), new AnalysisSpan(0, 2, 3, cold), new AnalysisSpan(1, 4, 5, plain)],
            pageCount: 2);
        var other = await TokenizedPageAsync(2, [new AnalysisSpan(0, 0, 1, hot)]);

        await _store.RequestAnalysisAsync([1], ReadingPriority.NextChapter);

        var rows = await _store.GetUnitAnalysisAsync(unitId);
        Assert.Equal([(0, 0, 1, hot), (0, 2, 3, cold)], rows.Select(r => (r.Block, r.S, r.E, r.WordId)));
        Assert.All(rows, r =>
        {
            Assert.Equal(AnalysisState.Pending, r.State);
            Assert.Equal(ReadingPriority.NextChapter, r.Priority);
        });
        // Page 1 was never tokenized, and file 2 has no request
        Assert.Equal(2, await ScalarAsync("SELECT COUNT(*) FROM analysis"));
        Assert.Empty(await _store.GetUnitAnalysisAsync(other));

        // Tokenizing the second page later gives it rows at the request's priority
        var page1 = (await _store.GetUnitAsync(1, 1))!;
        Assert.True(await SaveAsync(page1.Id, Stamp, [Block(0, "x")], [new AnalysisSpan(0, 0, 1, cold)]));
        Assert.Equal(ReadingPriority.NextChapter, Assert.Single(await _store.GetUnitAnalysisAsync(page1.Id)).Priority);
    }

    [Fact]
    public async Task RequestAnalysis_AgainLeavesRowsAndAnswersAndOnlyRaisesPendingRows()
    {
        var (hot, cold, _) = await SeedWordsAsync();
        var unitId = await TokenizedPageAsync(1,
            [new AnalysisSpan(0, 0, 1, hot), new AnalysisSpan(0, 2, 3, cold), new AnalysisSpan(0, 4, 5, hot)]);

        await _store.RequestAnalysisAsync([1], ReadingPriority.WholeBook);
        var first = await _store.GetUnitAnalysisAsync(unitId);
        await _store.StoreAnalysisAsync("gen", "p1", [new AnalysisAnswer(first[0].Id, "hot:2", "hot-r1")]);
        await _store.RaiseAnalysisPriorityAsync(1, 0, null, ReadingPriority.Tapped);

        await _store.RequestAnalysisAsync([1], ReadingPriority.OpenChapter);
        await _store.RequestAnalysisAsync([1, 1], ReadingPriority.WholeBook);

        var rows = await _store.GetUnitAnalysisAsync(unitId);
        Assert.Equal(first.Select(r => r.Id), rows.Select(r => r.Id));
        Assert.Equal((AnalysisState.Done, "hot:2", "hot-r1", "gen", ReadingPriority.WholeBook),
            (rows[0].State, rows[0].SenseKey, rows[0].Reading, rows[0].Generator, rows[0].Priority));
        Assert.All(rows.Skip(1), r =>
        {
            Assert.Equal(AnalysisState.Pending, r.State);
            Assert.Equal(ReadingPriority.Tapped, r.Priority);
        });
    }

    [Fact]
    public async Task RequestAnalysis_CoversABookLongerThanOneBatch()
    {
        var (hot, _, _) = await SeedWordsAsync();
        await _store.SeedFileAsync(1, 60, Stamp, 0);
        for (var page = 0; page < 60; page++)
        {
            var unit = (await _store.GetUnitAsync(1, page))!;
            Assert.True(await SaveAsync(unit.Id, Stamp, [Block(0, "abc")], [new AnalysisSpan(0, 0, 1, hot)]));
        }

        await _store.RequestAnalysisAsync([1], 0);

        Assert.Equal(60, await ScalarAsync("SELECT COUNT(*) FROM analysis"));
    }

    [Fact]
    public async Task RequestAnalysis_AfterAClearCreatesFreshRowsAtTheGivenPriority()
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
        Assert.True(await SaveAsync(unitId, NewStamp, [Block(0, "x")],
            [new AnalysisSpan(0, 0, 1, hot), new AnalysisSpan(0, 2, 3, cold)]));

        var added = (await _store.GetUnitAnalysisAsync(unitId))[1];
        Assert.Equal(AnalysisState.Pending, added.State);
        Assert.Equal(ReadingPriority.OpenChapter, added.Priority);
    }

    [Fact]
    public async Task RaiseAnalysisPriority_WithoutARequestQueuesNothing()
    {
        var (hot, _, _) = await SeedWordsAsync();
        var unitId = await TokenizedPageAsync(1, [new AnalysisSpan(0, 0, 1, hot)]);

        await _store.RaiseAnalysisPriorityAsync(1, 0, 0, ReadingPriority.Tapped);
        await _store.RaiseAnalysisPriorityAsync(1, null, null, ReadingPriority.Tapped);

        Assert.Empty(await _store.GetUnitAnalysisAsync(unitId));
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
        Assert.True(await SaveAsync(page0, Stamp, [Block(0, "a"), Block(1, "b")], spans));
        Assert.True(await SaveAsync(page1, Stamp, [Block(0, "a"), Block(1, "b")], spans));
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
        await _store.RequestAnalysisAsync([1], 0);
        var ids = (await _store.GetUnitAnalysisAsync(unitId)).Select(a => a.Id).ToArray();

        var outcome = await _store.StoreAnalysisAsync("claude-sonnet", "v2",
        [
            new AnalysisAnswer(ids[0], "hot:2", "hot-r1"),
            new AnalysisAnswer(ids[1], "cold:1", null),
            new AnalysisAnswer(ids[2], "cold:3", "cold-r9"),
            new AnalysisAnswer(ids[3], null, null),
            new AnalysisAnswer(ids[4], null, "hot-r0"),
            new AnalysisAnswer(ids[0], "hot:1", null),
            new AnalysisAnswer(ids[5] + 1000, null, null),
        ]);

        Assert.Equal(3, outcome.Stored);
        Assert.Equal(
        [
            new AnalysisRejection(ids[1], AnalysisRejectReason.SenseNotOffered),
            new AnalysisRejection(ids[2], AnalysisRejectReason.ReadingNotOffered),
            new AnalysisRejection(ids[0], AnalysisRejectReason.Duplicate),
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
            Assert.True(await SaveAsync(unit.Id, Stamp, [Block(0, "abc")], spans));
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
    public async Task ClearAnalysis_DeletesTheRowsAndTheRequest()
    {
        var (hot, cold, _) = await SeedWordsAsync();
        var unitId = await TokenizedPageAsync(1, [new AnalysisSpan(0, 0, 1, hot), new AnalysisSpan(0, 2, 3, hot)]);
        var other = await TokenizedPageAsync(2, [new AnalysisSpan(0, 0, 1, hot)]);
        await _store.RequestAnalysisAsync([1, 2], ReadingPriority.OpenChapter);
        var ids = (await _store.GetUnitAnalysisAsync(unitId)).Select(a => a.Id).ToArray();
        await _store.StoreAnalysisAsync("gen", "p1", [new AnalysisAnswer(ids[0], "hot:1", "hot-r1")]);

        await _store.ClearAnalysisAsync([1]);

        Assert.Empty(await _store.GetUnitAnalysisAsync(unitId));
        Assert.NotNull(await _store.GetUnitAsync(1, 0));
        Assert.Equal(AnalysisState.Pending, Assert.Single(await _store.GetUnitAnalysisAsync(other)).State);
        Assert.Equal(2, await ScalarAsync("SELECT manga_file_id FROM analysis_request"));

        // Without a request, a later tokenize adds no rows
        await _store.SeedFileAsync(1, 1, NewStamp, 0);
        Assert.True(await SaveAsync(unitId, NewStamp, [Block(0, "x")],
            [new AnalysisSpan(0, 4, 5, cold)]));
        Assert.Empty(await _store.GetUnitAnalysisAsync(unitId));
    }

    #endregion

    #region Stored JSON

    private async Task<string> RawAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string) (await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task StoredForm_CompressesBlocksAndKeepsWordJsonUnescaped()
    {
        var word = Word("食べる", senses: 2) with { Readings = ["たべる", "くう"] };
        var id = (await _store.UpsertWordsAsync([word], Lexicon))[0];
        await _store.SeedFileAsync(1, 1, Stamp, 0);
        var unit = (await _store.GetUnitAsync(1, 0))!;
        StoredBlock[] blocks =
        [
            Block(0, "「食べ物」を𠮟る & <b>", Token(1, 3, id, "たべもの", [new SubTokenDto(1, 2, id, "た"), new SubTokenDto(2, 3, null, null)])),
            Block(1, string.Empty),
        ];

        Assert.True(await _store.SaveTokenizedUnitAsync(unit.Id, Stamp, Tokenizer, blocks));

        Assert.Equal("blob", await RawAsync($"SELECT typeof(blocks) FROM unit WHERE id = {unit.Id}"));
        Assert.Equal($"{ReadingBlocks.BrotliJson:X2}", await RawAsync($"SELECT hex(substr(blocks, 1, 1)) FROM unit WHERE id = {unit.Id}"));
        Assert.Contains("たべる", await RawAsync($"SELECT readings FROM word WHERE id = {id}"));
        Assert.Contains("食べる", await RawAsync($"SELECT senses FROM word WHERE id = {id}"));

        var stored = (await _store.GetUnitAsync(1, 0))!.Blocks;
        Assert.Equal(blocks.Select(b => (b.Index, b.Text)), stored.Select(b => (b.Index, b.Text)));
        var compound = Assert.Single(stored[0].Tokens);
        Assert.Equal((1, 3, (long?) id, "たべもの"), (compound.S, compound.E, compound.W, compound.R));
        Assert.Equal(blocks[0].Tokens[0].Sub, compound.Sub);
        Assert.Empty(stored[1].Tokens);
        var read = (await _store.GetWordsAsync([id]))[id];
        Assert.Equal(word.Readings, read.Readings);
        Assert.Equal(word.Senses.Select(s => s.Key), read.Senses.Select(s => s.Key));
    }

    [Fact]
    public async Task StoredForm_KeepsPhrasesAndLeavesTheKeyOutOfABlockWithout()
    {
        var (hot, _, plain) = await SeedWordsAsync();
        await _store.SeedFileAsync(1, 1, Stamp, 0);
        var unit = (await _store.GetUnitAsync(1, 0))!;
        StoredBlock[] blocks =
        [
            Block(0, "気がした", Token(0, 1, plain, "き"), Token(1, 2, null), Token(2, 3, plain, "し"), Token(3, 4, null))
                with { Phrases = [new PhraseDto(0, 3, hot), new PhraseDto(1, 3, plain)] },
            Block(1, "猫", Token(0, 1, plain)),
        ];

        Assert.True(await _store.SaveTokenizedUnitAsync(unit.Id, Stamp, Tokenizer, blocks));

        var stored = (await _store.GetUnitAsync(1, 0))!.Blocks;
        Assert.Equal([new PhraseDto(0, 3, hot), new PhraseDto(1, 3, plain)], stored[0].Phrases);
        Assert.Empty(stored[1].Phrases);
        var json = await DecompressedBlocksAsync(unit.Id);
        using var document = JsonDocument.Parse(json);
        Assert.Equal($"[[0,3,{hot}],[1,3,{plain}]]", document.RootElement[0].GetProperty("phrases").GetRawText());
        Assert.False(document.RootElement[1].TryGetProperty("phrases", out _));
    }

    [Fact]
    public async Task StoredForm_KeepsBlindOffsetsAndLeavesTheKeyOutOfABlockWithout()
    {
        var (_, _, plain) = await SeedWordsAsync();
        await _store.SeedFileAsync(1, 1, Stamp, 0);
        var unit = (await _store.GetUnitAsync(1, 0))!;
        StoredBlock[] blocks =
        [
            Block(0, "星が瞬く", Token(0, 1, plain), Token(1, 2, null), Token(2, 4, plain, "しばたたく")) with { Blind = [2] },
            Block(1, "猫", Token(0, 1, plain)),
        ];

        Assert.True(await _store.SaveTokenizedUnitAsync(unit.Id, Stamp, Tokenizer, blocks));

        var stored = (await _store.GetUnitAsync(1, 0))!.Blocks;
        Assert.Equal([2], stored[0].Blind);
        Assert.Empty(stored[1].Blind);
        using var document = JsonDocument.Parse(await DecompressedBlocksAsync(unit.Id));
        Assert.Equal("[2]", document.RootElement[0].GetProperty("blind").GetRawText());
        Assert.False(document.RootElement[1].TryGetProperty("blind", out _));
        Assert.Equal(ReadingBlocks.BrotliJson, (await RawBlocksAsync(unit.Id))[0]);
    }

    [Fact]
    public async Task StoredForm_KeepsRubyAndLeavesTheKeyOutOfABlockWithout()
    {
        var (_, _, plain) = await SeedWordsAsync();
        await _store.SeedFileAsync(1, 1, Stamp, 0);
        var unit = (await _store.GetUnitAsync(1, 0))!;
        StoredBlock[] blocks =
        [
            Block(0, "高順は漢字", Token(0, 2, plain), Token(2, 3, null), Token(3, 5, plain))
                with { Ruby = [new RubyDto(0, 2, "ガオシュン"), new RubyDto(3, 4, "かん"), new RubyDto(4, 5, "じ")] },
            Block(1, "猫", Token(0, 1, plain)),
        ];

        Assert.True(await _store.SaveTokenizedUnitAsync(unit.Id, Stamp, Tokenizer, blocks));

        var stored = (await _store.GetUnitAsync(1, 0))!.Blocks;
        Assert.Equal(blocks[0].Ruby, stored[0].Ruby);
        Assert.Empty(stored[1].Ruby);
        var json = await DecompressedBlocksAsync(unit.Id);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("""[[0,2,"ガオシュン"],[3,4,"かん"],[4,5,"じ"]]""",
            document.RootElement[0].GetProperty("ruby").GetRawText());
        Assert.False(document.RootElement[1].TryGetProperty("ruby", out _));
        // The format byte is the one blocks had before ruby, so nothing needs rewriting
        Assert.Equal(ReadingBlocks.BrotliJson, (await RawBlocksAsync(unit.Id))[0]);
    }

    private async Task<byte[]> RawBlocksAsync(long unitId)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT blocks FROM unit WHERE id = {unitId}";
        return (byte[]) (await command.ExecuteScalarAsync())!;
    }

    private async Task<string> DecompressedBlocksAsync(long unitId)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT blocks FROM unit WHERE id = {unitId}";
        var blob = (byte[]) (await command.ExecuteScalarAsync())!;
        using var brotli = new System.IO.Compression.BrotliStream(new MemoryStream(blob, 1, blob.Length - 1),
            System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(brotli);
        return await reader.ReadToEndAsync();
    }

    private async Task SetRawBlocksAsync(long unitId, object value)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE unit SET blocks = $blocks, state = 'done' WHERE id = $unit";
        command.Parameters.AddWithValue("$blocks", value);
        command.Parameters.AddWithValue("$unit", unitId);
        await command.ExecuteNonQueryAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoredForm_ReadsBlocksWrittenAsJsonText(bool escaped)
    {
        var (hot, _, _) = await SeedWordsAsync();
        await _store.SeedFileAsync(1, 1, Stamp, 0);
        var unit = (await _store.GetUnitAsync(1, 0))!;
        object[] value = [new { i = 0, text = "食べ物", tokens = new object[] { new object?[] { 0, 3, hot, "たべもの", null } } }];
        var json = escaped ? JsonSerializer.Serialize(value) : JsonSerializer.Serialize(value, ReadingJson.Options);
        Assert.Equal(escaped, json.Contains("\\u98DF"));
        await SetRawBlocksAsync(unit.Id, json);

        var block = (await _store.GetUnitAsync(1, 0))!.Blocks.Single();
        Assert.Equal("食べ物", block.Text);
        Assert.Equal(Token(0, 3, hot, "たべもの"), block.Tokens.Single());
        // Blocks written before phrases and ruby existed have neither key
        Assert.Empty(block.Phrases);
        Assert.Empty(block.Ruby);

        // The other two reads of blocks take the same form
        await _store.RequestAnalysisAsync([1], 0);
        Assert.Equal("食べ物", Assert.Single(await _store.GetPendingAnalysisAsync(10, 0)).BlockText);
    }

    [Fact]
    public async Task StoredForm_ReadsWordJsonWrittenWithEscapes()
    {
        var id = (await _store.UpsertWordsAsync([Word("x")], Lexicon))[0];
        await using (var connection = await OpenAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE word SET readings = $readings WHERE id = $word";
            command.Parameters.AddWithValue("$readings", JsonSerializer.Serialize(new[] { "たべる" }));
            command.Parameters.AddWithValue("$word", id);
            await command.ExecuteNonQueryAsync();
        }

        Assert.Equal(["たべる"], (await _store.GetWordsAsync([id]))[id].Readings);
    }

    [Theory]
    [InlineData(new byte[] { 0x01, 0xFF, 0x00, 0x13, 0x37 })]
    [InlineData(new byte[] { 0x7E, 0x5B, 0x5D })]
    public async Task StoredForm_ADamagedBlobNamesItsUnit(byte[] blob)
    {
        await _store.SeedFileAsync(1, 1, Stamp, 0);
        var unit = (await _store.GetUnitAsync(1, 0))!;
        await SetRawBlocksAsync(unit.Id, blob);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => _store.GetUnitAsync(1, 0));

        Assert.Contains($"unit {unit.Id}", ex.Message);
    }

    [Theory]
    [InlineData("""[{"i": 0, "text": "猫"}, {"i": 1, "text": "犬", "tokens": null}]""")]
    public async Task StoredForm_ABlockWithoutTokensReadsAsEmpty(string json)
    {
        await _store.SeedFileAsync(1, 1, Stamp, 0);
        await _store.RequestAnalysisAsync([1], 0);
        var unit = (await _store.GetUnitAsync(1, 0))!;
        await SetRawBlocksAsync(unit.Id, json);

        var blocks = (await _store.GetUnitAsync(1, 0))!.Blocks;

        Assert.Equal(["猫", "犬"], blocks.Select(b => b.Text));
        Assert.All(blocks, b => Assert.Empty(b.Tokens));
        // Deriving spans walks the tokens, and finds none to queue
        await _store.SeedFileAsync(1, 1, NewStamp, 0);
        Assert.True(await _store.SaveTokenizedUnitAsync(unit.Id, NewStamp, Tokenizer, blocks));
        await _store.ClearAnalysisAsync([1]);
        await SetRawBlocksAsync(unit.Id, json);
        await _store.RequestAnalysisAsync([1], 0);
        Assert.Empty(await _store.GetUnitAnalysisAsync(unit.Id));
    }

    #endregion

    #region Reading marks

    private sealed record MarkWords(long Name, long Say, long Open, long Part, long Phrase);

    private async Task<MarkWords> SeedMarkWordsAsync()
    {
        var ids = await _store.UpsertWordsAsync([Word("高順"), Word("言う"), Word("開く"), Word("開"), Word("言うまでもない")], Lexicon);
        return new MarkWords(ids[0], ids[1], ids[2], ids[3], ids[4]);
    }

    /// <summary>
    /// 高順 three times, twice printed in katakana and once in hiragana, 言っ marked and 言う unmarked, and 開く marked
    /// as a compound whose part has a word. A phrase over 言う and a token of an unknown word count for nothing
    /// </summary>
    private static StoredBlock[] MarkedPage(MarkWords w) =>
    [
        Block(0, "高順は言った。高順と高順", Token(0, 2, w.Name), Token(2, 3, null), Token(3, 5, w.Say), Token(5, 6, null),
                Token(6, 7, null), Token(7, 9, w.Name), Token(9, 10, null), Token(10, 12, w.Name))
            with
            {
                Ruby = [new RubyDto(0, 2, "ガオシュン"), new RubyDto(3, 4, "い"), new RubyDto(7, 9, "ガオシュン"), new RubyDto(10, 12, "がおしゅん")],
                Phrases = [new PhraseDto(3, 6, w.Phrase)],
            },
        Block(1, "言う", Token(0, 2, w.Say)),
        Block(2, "開く謎", Token(0, 2, w.Open, sub: [new SubTokenDto(0, 1, w.Part, "あ"), new SubTokenDto(1, 2, null, "く")]),
                Token(2, 3, 9999))
            with { Ruby = [new RubyDto(0, 1, "あ"), new RubyDto(2, 3, "なぞ")] },
    ];

    private async Task<List<(long Unit, long Word, string Reading, string Printed, long Count)>> MarkRowsAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT unit_id, word_id, reading, printed, count FROM reading_mark ORDER BY unit_id, word_id, reading";
        var rows = new List<(long, long, string, string, long)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4)));
        }
        return rows;
    }

    private async Task<List<(long Unit, long Word, long Uses)>> UseRowsAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT unit_id, word_id, uses FROM word_use ORDER BY unit_id, word_id";
        var rows = new List<(long, long, long)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)));
        return rows;
    }

    private async Task<long> SaveMarkedPageAsync(int file, int page, IReadOnlyList<StoredBlock> blocks, int pageCount = 2)
    {
        if (page == 0) await _store.SeedFileAsync(file, pageCount, Stamp, 0);
        var unit = (await _store.GetUnitAsync(file, page))!;
        Assert.True(await _store.SaveTokenizedUnitAsync(unit.Id, Stamp, Tokenizer, blocks));
        return unit.Id;
    }

    [Fact]
    public async Task SaveTokenizedUnit_CountsTopLevelUsesAndDictionaryFormMarks()
    {
        var w = await SeedMarkWordsAsync();

        var unit = await SaveMarkedPageAsync(1, 0, MarkedPage(w));

        Assert.Equal(
        [
            (unit, w.Name, "がおしゅん", "ガオシュン", 3L),
            (unit, w.Say, "いう", "いう", 1L),
            (unit, w.Open, "あく", "あく", 1L),
        ], await MarkRowsAsync());
        Assert.Equal([(unit, w.Name, 3L), (unit, w.Say, 2L), (unit, w.Open, 1L), (unit, 9999L, 1L)], await UseRowsAsync());
    }

    [Fact]
    public async Task SaveTokenizedUnit_WithoutRuby_CountsUsesAndNoMarks()
    {
        var w = await SeedMarkWordsAsync();

        var unit = await SaveMarkedPageAsync(1, 0, MarkedPage(w).Select(b => b with { Ruby = [] }).ToList());

        Assert.Empty(await MarkRowsAsync());
        Assert.Contains((unit, w.Name, 3L), await UseRowsAsync());
    }

    [Fact]
    public async Task SaveTokenizedUnit_AgainReplacesOnlyThatPagesRows()
    {
        var w = await SeedMarkWordsAsync();
        var first = await SaveMarkedPageAsync(1, 0, MarkedPage(w));
        var second = await SaveMarkedPageAsync(1, 1, MarkedPage(w));

        Assert.True(await _store.SaveTokenizedUnitAsync(first, Stamp, Tokenizer,
            [Block(0, "開か", Token(0, 2, w.Open)) with { Ruby = [new RubyDto(0, 1, "ひら")] }]));

        var marks = await MarkRowsAsync();
        Assert.Equal([(first, w.Open, "ひらく", "ひらく", 1L)], marks.Where(r => r.Unit == first));
        Assert.Equal(3, marks.Count(r => r.Unit == second));
        Assert.Equal([(first, w.Open, 1L)], (await UseRowsAsync()).Where(r => r.Unit == first));
        Assert.Equal(4, (await UseRowsAsync()).Count(r => r.Unit == second));
    }

    [Fact]
    public async Task SaveTokenizedUnit_ThatIsRefusedWritesNoRows()
    {
        var w = await SeedMarkWordsAsync();
        await _store.SeedFileAsync(1, 1, Stamp, 0);
        var unit = (await _store.GetUnitAsync(1, 0))!;

        Assert.False(await _store.SaveTokenizedUnitAsync(unit.Id, NewStamp, Tokenizer, MarkedPage(w)));

        Assert.Empty(await MarkRowsAsync());
        Assert.Empty(await UseRowsAsync());
    }

    [Fact]
    public async Task DeletingUnits_RemovesTheirRows_AndReseedingKeepsThem()
    {
        var w = await SeedMarkWordsAsync();
        var first = await SaveMarkedPageAsync(1, 0, MarkedPage(w), pageCount: 3);
        await SaveMarkedPageAsync(1, 1, MarkedPage(w));
        var last = await SaveMarkedPageAsync(1, 2, MarkedPage(w));
        var other = await SaveMarkedPageAsync(2, 0, MarkedPage(w));

        // A new stamp keeps the rows until the page is tokenized again, and fewer pages delete the rest
        await _store.SeedFileAsync(1, 2, NewStamp, 0);
        Assert.DoesNotContain(await MarkRowsAsync(), r => r.Unit == last);
        Assert.DoesNotContain(await UseRowsAsync(), r => r.Unit == last);
        Assert.Contains(await MarkRowsAsync(), r => r.Unit == first);

        await _store.DeleteFilesAsync([1]);
        Assert.Equal([other], (await MarkRowsAsync()).Select(r => r.Unit).Distinct());
        Assert.Equal([other], (await UseRowsAsync()).Select(r => r.Unit).Distinct());
    }

    [Fact]
    public async Task GetReadingMarks_SumsOneFilesPagesForTheAskedWords()
    {
        var w = await SeedMarkWordsAsync();
        await SaveMarkedPageAsync(1, 0, MarkedPage(w));
        await SaveMarkedPageAsync(1, 1, [
            Block(0, "高順", Token(0, 2, w.Name)) with { Ruby = [new RubyDto(0, 2, "こうじゅん")] },
            Block(1, "開く", Token(0, 2, w.Open)),
        ]);
        await SaveMarkedPageAsync(2, 0, MarkedPage(w));

        var totals = await _store.GetReadingMarksAsync(1, [w.Name, w.Open, w.Part, 12345]);

        Assert.Equal([w.Name, w.Open], totals.Keys.Order());
        Assert.Equal(4, totals[w.Name].Uses);
        Assert.Equal([new ReadingMarkTotal("がおしゅん", "ガオシュン", 3), new ReadingMarkTotal("こうじゅん", "こうじゅん", 1)],
            totals[w.Name].Readings);
        Assert.Equal(2, totals[w.Open].Uses);
        Assert.Equal([new ReadingMarkTotal("あく", "あく", 1)], totals[w.Open].Readings);
    }

    [Fact]
    public async Task GetReadingMarks_SumsEveryFileAndPicksThePrintedFormCountedMost()
    {
        var w = await SeedMarkWordsAsync();
        await SaveMarkedPageAsync(1, 0, MarkedPage(w));
        // Four hiragana marks in file 2 outweigh file 1's three katakana ones
        await SaveMarkedPageAsync(2, 0, [
            Block(0, "高順高順高順高順", Token(0, 2, w.Name), Token(2, 4, w.Name), Token(4, 6, w.Name), Token(6, 8, w.Name))
                with { Ruby = [new RubyDto(0, 2, "がおしゅん"), new RubyDto(2, 4, "がおしゅん"), new RubyDto(4, 6, "がおしゅん"), new RubyDto(6, 8, "がおしゅん")] },
        ]);
        await SaveMarkedPageAsync(3, 0, MarkedPage(w));

        var both = await _store.GetReadingMarksAsync([1, 2, 2], [w.Name, w.Say]);

        Assert.Equal(7, both[w.Name].Uses);
        Assert.Equal([new ReadingMarkTotal("がおしゅん", "がおしゅん", 7)], both[w.Name].Readings);
        Assert.Equal(2, both[w.Say].Uses);
        Assert.Equal([new ReadingMarkTotal("いう", "いう", 1)], both[w.Say].Readings);
        Assert.Equal(["ガオシュン"], (await _store.GetReadingMarksAsync([1], [w.Name]))[w.Name].Readings.Select(r => r.Printed));
    }

    [Fact]
    public async Task GetReadingMarks_GivesAnEmptyAnswerForNothingAskedAndBatchesLongLists()
    {
        var w = await SeedMarkWordsAsync();
        await SaveMarkedPageAsync(1, 0, MarkedPage(w));

        Assert.Empty(await _store.GetReadingMarksAsync([], [w.Name]));
        Assert.Empty(await _store.GetReadingMarksAsync([1], []));
        Assert.Empty(await _store.GetReadingMarksAsync(7, [w.Name]));

        var manyWords = Enumerable.Range(100_000, 1200).Select(i => (long) i).Append(w.Name).ToList();
        var manyFiles = Enumerable.Range(10, 1200).Append(1).ToList();
        var totals = await _store.GetReadingMarksAsync(manyFiles, manyWords);
        Assert.Equal(3, Assert.Single(totals).Value.Uses);
        Assert.Equal(3, totals[w.Name].Readings.Single().Count);
    }

    /// <summary>
    /// What the hints for one page cost: the page's 400 words asked of its file, and then of all ten files of its
    /// series, the two reads <see cref="DictionaryService"/> makes. Ten volumes of 60 pages, each page using 400 of
    /// 4,000 words. The first case lets any word be marked, on one use in ten, which is far more furigana than a book
    /// prints. The second marks one word in 20, on half its uses, closer to a book that marks rare words and names
    /// </summary>
    [Theory]
    [InlineData(1, 10)]
    [InlineData(20, 2)]
    public async Task GetReadingMarks_ForAPageOf400WordsInATenVolumeSeries_IsFast(int markedWordEvery, int markedUseEvery)
    {
        const int files = 10, pages = 60, wordsPerPage = 400, vocabulary = 4000;
        var random = new Random(12);
        await using (var connection = await OpenAsync())
        {
            await using var transaction = connection.BeginTransaction();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO unit (kind, manga_file_id, page, source_stamp, state, priority, requested_utc)
                VALUES ('epub', $file, $page, 's', 'done', 0, 0) RETURNING id
                """;
            var file = command.Parameters.Add("$file", SqliteType.Integer);
            var page = command.Parameters.Add("$page", SqliteType.Integer);

            await using var use = connection.CreateCommand();
            use.Transaction = transaction;
            use.CommandText = "INSERT INTO word_use (unit_id, word_id, uses) VALUES ($unit, $word, $uses)";
            var useUnit = use.Parameters.Add("$unit", SqliteType.Integer);
            var useWord = use.Parameters.Add("$word", SqliteType.Integer);
            var uses = use.Parameters.Add("$uses", SqliteType.Integer);

            await using var mark = connection.CreateCommand();
            mark.Transaction = transaction;
            mark.CommandText = "INSERT INTO reading_mark (unit_id, word_id, reading, printed, count) VALUES ($unit, $word, 'よみ', 'ヨミ', 1)";
            var markUnit = mark.Parameters.Add("$unit", SqliteType.Integer);
            var markWord = mark.Parameters.Add("$word", SqliteType.Integer);

            for (var f = 1; f <= files; f++)
            for (var p = 0; p < pages; p++)
            {
                file.Value = f;
                page.Value = p;
                var unitId = Convert.ToInt64(await command.ExecuteScalarAsync());
                useUnit.Value = markUnit.Value = unitId;
                foreach (var w in Enumerable.Range(1, vocabulary).OrderBy(_ => random.Next()).Take(wordsPerPage))
                {
                    useWord.Value = markWord.Value = w;
                    uses.Value = random.Next(1, 4);
                    await use.ExecuteNonQueryAsync();
                    if (w % markedWordEvery == 0 && random.Next(markedUseEvery) == 0) await mark.ExecuteNonQueryAsync();
                }
            }
            await transaction.CommitAsync();
        }

        var pageWords = Enumerable.Range(1, vocabulary).OrderBy(_ => random.Next()).Take(wordsPerPage).Select(w => (long) w).ToList();
        var seriesFiles = Enumerable.Range(1, files).ToList();
        var times = new List<double>();
        for (var run = 0; run < 6; run++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var book = await _store.GetReadingMarksAsync(1, pageWords, markedOnly: true);
            var series = await _store.GetReadingMarksAsync(seriesFiles, pageWords, markedOnly: true);
            watch.Stop();
            Assert.NotEmpty(book);
            Assert.True(series.Values.Sum(t => t.Uses) > book.Values.Sum(t => t.Uses));
            // The first run warms the pool and the page cache
            if (run > 0) times.Add(watch.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        _output.WriteLine($"Hint reads, marks on 1/{markedWordEvery} of words and 1/{markedUseEvery} of their uses: median {times[times.Count / 2]:0.0} ms, " +
                         $"min {times[0]:0.0} ms, max {times[^1]:0.0} ms");
        Assert.True(times[times.Count / 2] < 500, $"The hint reads took {times[times.Count / 2]:0} ms");
    }

    #endregion

    #region Book words

    /// <summary>
    /// 翠苓 under スイレイ twice and once under すいれい, 馬閃 under バセン, and 脱出方法 under its pieces' reading,
    /// which defines nothing
    /// </summary>
    private static StoredBlock[] BookWordPage(long piece) =>
    [
        Block(0, "翠苓と翠苓", Token(0, 1, piece), Token(1, 2, piece), Token(2, 3, null), Token(3, 4, piece), Token(4, 5, piece))
            with { Ruby = [new RubyDto(0, 2, "スイレイ"), new RubyDto(3, 5, "スイレイ")] },
        Block(1, "翠苓、馬閃", Token(0, 1, piece), Token(1, 2, piece), Token(2, 3, null), Token(3, 4, piece, "うま"),
                Token(4, 5, piece, "せん"))
            with { Ruby = [new RubyDto(0, 2, "すいれい"), new RubyDto(3, 5, "バセン")] },
        Block(2, "脱出方法", Token(0, 2, piece, "だっしゅつ"), Token(2, 4, piece, "ほうほう"))
            with { Ruby = [new RubyDto(0, 4, "だっしゅつほうほう")] },
    ];

    private async Task<List<(long Unit, string Text, string Reading, string Printed, long Count)>> BookWordRowsAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT unit_id, text, reading, printed, count FROM book_word ORDER BY unit_id, text, reading";
        var rows = new List<(long, string, string, string, long)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4)));
        }
        return rows;
    }

    [Fact]
    public async Task SaveTokenizedUnit_StoresThePagesBookWordsAndAWordRowForEach()
    {
        var piece = (await _store.UpsertWordsAsync([Word("翠")], Lexicon))[0];

        var unit = await SaveMarkedPageAsync(1, 0, BookWordPage(piece));

        Assert.Equal([(unit, "翠苓", "すいれい", "スイレイ", 3L), (unit, "馬閃", "ばせん", "バセン", 1L)], await BookWordRowsAsync());
        var totals = await _store.GetBookWordsAsync([1]);
        var words = await _store.GetWordsAsync(totals.Select(t => t.WordId).ToList());
        var suirei = words[totals.Single(t => t.Text == "翠苓").WordId];
        Assert.Equal(("翠苓", BookWords.Pos, true, "すいれい", BookWords.LexiconVersion), (suirei.Lemma, suirei.Pos,
            suirei.IsProper, suirei.Reading, suirei.LexiconVersion));
        Assert.Empty(suirei.Senses);
        // Empty and not null, so the resolver tells it from a word stored before entries
        Assert.NotNull(suirei.Entries);
        Assert.Empty(suirei.Entries);
    }

    [Fact]
    public async Task SaveTokenizedUnit_KeepsAWordRowTheSidecarAlreadyWrote()
    {
        var known = new SidecarWordDto
        {
            Lemma = "馬閃", Pos = BookWords.Pos, IsProper = true, Reading = "ばせん", Readings = ["ばせん"],
            Senses = [new SenseDto("5000:1", ["n-pr"], ["Basen"])], Entries = [new WordEntryDto(5000, false, ["ばせん"])],
        };
        var ids = await _store.UpsertWordsAsync([known, Word("翠")], Lexicon);
        var id = ids[0];

        await SaveMarkedPageAsync(1, 0, BookWordPage(ids[1]));

        var word = (await _store.GetWordsAsync([id]))[id];
        Assert.Equal(Lexicon, word.LexiconVersion);
        Assert.Equal("5000:1", Assert.Single(word.Senses).Key);
        Assert.Equal(id, (await _store.GetBookWordsAsync([1])).Single(t => t.Text == "馬閃").WordId);
    }

    [Fact]
    public async Task SaveTokenizedUnit_AgainReplacesThePagesBookWords_AndDeletingUnitsRemovesThem()
    {
        var piece = (await _store.UpsertWordsAsync([Word("翠")], Lexicon))[0];
        var first = await SaveMarkedPageAsync(1, 0, BookWordPage(piece), pageCount: 3);
        var second = await SaveMarkedPageAsync(1, 1, BookWordPage(piece));
        var last = await SaveMarkedPageAsync(1, 2, BookWordPage(piece));
        var other = await SaveMarkedPageAsync(2, 0, BookWordPage(piece));

        Assert.True(await _store.SaveTokenizedUnitAsync(first, Stamp, Tokenizer, [Block(0, "翠苓", Token(0, 1, piece), Token(1, 2, piece))]));
        Assert.DoesNotContain(await BookWordRowsAsync(), r => r.Unit == first);
        Assert.Equal(2, (await BookWordRowsAsync()).Count(r => r.Unit == second));

        await _store.SeedFileAsync(1, 2, NewStamp, 0);
        Assert.DoesNotContain(await BookWordRowsAsync(), r => r.Unit == last);
        Assert.Contains(await BookWordRowsAsync(), r => r.Unit == second);

        await _store.DeleteFilesAsync([1]);
        Assert.Equal([other], (await BookWordRowsAsync()).Select(r => r.Unit).Distinct());
    }

    [Fact]
    public async Task GetBookWords_SumsEachFilesPagesAndKeepsFilesApart()
    {
        var piece = (await _store.UpsertWordsAsync([Word("翠")], Lexicon))[0];
        await SaveMarkedPageAsync(1, 0, BookWordPage(piece));
        // Page 1 prints すいれい three times, so the file's printed form for it is a tie at three that ordinal order settles
        await SaveMarkedPageAsync(1, 1, [
            Block(0, "翠苓翠苓翠苓", Token(0, 1, piece), Token(1, 2, piece), Token(2, 3, piece), Token(3, 4, piece),
                    Token(4, 5, piece), Token(5, 6, piece))
                with { Ruby = [new RubyDto(0, 2, "すいれい"), new RubyDto(2, 4, "すいれい"), new RubyDto(4, 6, "すいれい")] },
        ]);
        await SaveMarkedPageAsync(2, 0, [
            Block(0, "翠苓", Token(0, 1, piece), Token(1, 2, piece)) with { Ruby = [new RubyDto(0, 2, "ミドリ")] },
        ]);
        await SaveMarkedPageAsync(3, 0, BookWordPage(piece));

        var totals = await _store.GetBookWordsAsync([1, 2, 2]);

        var id = totals[0].WordId;
        Assert.Equal(
        [
            new BookWordTotal(1, "翠苓", id, "すいれい", "すいれい", 6),
            new BookWordTotal(1, "馬閃", totals[1].WordId, "ばせん", "バセン", 1),
            new BookWordTotal(2, "翠苓", id, "みどり", "ミドリ", 1),
        ], totals);
        Assert.Empty(await _store.GetBookWordsAsync([]));
        Assert.Empty(await _store.GetBookWordsAsync([9]));
        Assert.Equal(5, (await _store.GetBookWordsAsync(Enumerable.Range(10, 1200).Append(1).Append(2).Append(3).ToList())).Count);
    }

    /// <summary>
    /// What reading a series' book words costs a page. Ten volumes of 60 pages, with 200 book words in the series and
    /// six rows on each page, more than the 30 a volume of 薬屋 defines
    /// </summary>
    [Fact]
    public async Task GetBookWords_ForATenVolumeSeries_IsFast()
    {
        const int files = 10, pages = 60, words = 200, rowsPerPage = 6;
        var random = new Random(3);
        await using (var connection = await OpenAsync())
        {
            await using var transaction = connection.BeginTransaction();
            await using var word = connection.CreateCommand();
            word.Transaction = transaction;
            word.CommandText = """
                INSERT INTO word (lemma, pos, is_proper, reading, readings, senses, lexicon_version, entries)
                VALUES ($lemma, '名詞', 1, 'よみ', '[]', '[]', 'book', '[]')
                """;
            var lemma = word.Parameters.Add("$lemma", SqliteType.Text);
            for (var w = 0; w < words; w++)
            {
                lemma.Value = $"名{w}";
                await word.ExecuteNonQueryAsync();
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO unit (kind, manga_file_id, page, source_stamp, state, priority, requested_utc)
                VALUES ('epub', $file, $page, 's', 'done', 0, 0) RETURNING id
                """;
            var file = command.Parameters.Add("$file", SqliteType.Integer);
            var page = command.Parameters.Add("$page", SqliteType.Integer);

            await using var row = connection.CreateCommand();
            row.Transaction = transaction;
            row.CommandText = """
                INSERT OR IGNORE INTO book_word (unit_id, text, reading, printed, count) VALUES ($unit, $text, 'よみ', 'ヨミ', 1)
                """;
            var unit = row.Parameters.Add("$unit", SqliteType.Integer);
            var text = row.Parameters.Add("$text", SqliteType.Text);
            for (var f = 1; f <= files; f++)
            for (var p = 0; p < pages; p++)
            {
                file.Value = f;
                page.Value = p;
                unit.Value = Convert.ToInt64(await command.ExecuteScalarAsync());
                for (var r = 0; r < rowsPerPage; r++)
                {
                    text.Value = $"名{random.Next(words)}";
                    await row.ExecuteNonQueryAsync();
                }
            }
            await transaction.CommitAsync();
        }

        // A file id with no rows makes each run a new set of files, so it misses the cache
        async Task<List<double>> TimeAsync(Func<int, List<int>> filesFor)
        {
            var times = new List<double>();
            for (var run = 0; run < 7; run++)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var set = BookWordSet.From(await _store.GetBookWordsAsync(filesFor(run)), 1);
                watch.Stop();
                Assert.Equal(words, set.Words.Count);
                // The first run warms the pool and the page cache
                if (run > 0) times.Add(watch.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            return times;
        }

        var missed = await TimeAsync(run => [.. Enumerable.Range(1, files), 1000 + run]);
        var cached = await TimeAsync(_ => [.. Enumerable.Range(1, files)]);

        _output.WriteLine($"Book words of a ten-volume series, read and reduced: median {missed[missed.Count / 2]:0.0} ms, " +
                          $"max {missed[^1]:0.0} ms. From the cache: median {cached[cached.Count / 2]:0.00} ms");
        Assert.True(missed[missed.Count / 2] < 100, $"The book word read took {missed[missed.Count / 2]:0} ms");
        Assert.True(cached[cached.Count / 2] < missed[missed.Count / 2]);
    }

    [Fact]
    public async Task GetBookWords_FromTheCache_IsDroppedWhenAPageOfOneOfTheFilesIsSaved()
    {
        var piece = (await _store.UpsertWordsAsync([Word("翠")], Lexicon))[0];
        await SaveMarkedPageAsync(1, 0, BookWordPage(piece));
        await SaveMarkedPageAsync(2, 0, []);
        await SaveMarkedPageAsync(3, 0, []);
        Assert.Equal(2, (await _store.GetBookWordsAsync([1, 2])).Count);
        Assert.Empty(await _store.GetBookWordsAsync([3]));

        // Page 1 of file 2 gains 馬閃, and file 3's cached answer isn't touched by it
        var unit = (await _store.GetUnitAsync(2, 1))!;
        Assert.True(await _store.SaveTokenizedUnitAsync(unit.Id, Stamp, Tokenizer, [BookWordPage(piece)[1]]));

        Assert.Equal(["翠苓", "馬閃", "翠苓", "馬閃"], (await _store.GetBookWordsAsync([2, 1])).Select(t => t.Text));
        Assert.Empty(await _store.GetBookWordsAsync([3]));

        await _store.DeleteFilesAsync([1]);
        Assert.Equal(2, (await _store.GetBookWordsAsync([1, 2])).Count);
        await _store.SeedFileAsync(2, 1, Stamp, 0);
        Assert.Empty(await _store.GetBookWordsAsync([1, 2]));
    }

    #endregion

    #region Reading choices

    [Fact]
    public async Task ReadingChoices_SaveReplaceAndClearOnlyTheUsersOwnRowInThatSeries()
    {
        await _store.SaveReadingChoiceAsync(1, 10, 100, "またたく");
        await _store.SaveReadingChoiceAsync(1, 10, 101, "うわて");
        await _store.SaveReadingChoiceAsync(2, 10, 100, "しばたたく");
        await _store.SaveReadingChoiceAsync(1, 11, 100, "まばたく");

        _time.Advance();
        await _store.SaveReadingChoiceAsync(1, 10, 100, "まばたく");

        Assert.Equal(new Dictionary<long, string> { [100] = "まばたく", [101] = "うわて" },
            await _store.GetReadingChoicesAsync(1, 10, [100, 101, 102]));
        Assert.Equal(new Dictionary<long, string> { [100] = "しばたたく" }, await _store.GetReadingChoicesAsync(2, 10, [100, 101]));
        Assert.Equal(4, await ScalarAsync("SELECT COUNT(*) FROM reading_choice"));
        Assert.Equal(_time.Now.UtcTicks,
            await ScalarAsync("SELECT updated_utc FROM reading_choice WHERE user_id = 1 AND series_id = 10 AND word_id = 100"));

        await _store.ClearReadingChoiceAsync(1, 10, 100);
        await _store.ClearReadingChoiceAsync(1, 10, 999);

        Assert.Equal(new Dictionary<long, string> { [101] = "うわて" }, await _store.GetReadingChoicesAsync(1, 10, [100, 101]));
        Assert.Equal(new Dictionary<long, string> { [100] = "まばたく" }, await _store.GetReadingChoicesAsync(1, 11, [100]));
        Assert.Empty(await _store.GetReadingChoicesAsync(1, 10, []));
    }

    [Fact]
    public async Task ReadingChoices_ReadMoreWordsThanOneBatchAndOutliveTheSeriesFiles()
    {
        foreach (var word in Enumerable.Range(1, 1200)) await _store.SaveReadingChoiceAsync(1, 10, word, "よみ");
        await TokenizedPageAsync(1, []);

        await _store.DeleteFilesAsync([1]);

        Assert.Equal(1200, (await _store.GetReadingChoicesAsync(1, 10, Enumerable.Range(1, 1200).Select(w => (long) w).ToList())).Count);
    }

    #endregion

    #region Counts

    [Fact]
    public async Task GetCounts_CountsByStateForTheWholeStoreOrSomeFiles()
    {
        Assert.Equal(new ReadingStoreCounts(0, 0, 0, 0, 0), await _store.GetCountsAsync(null));

        var (hot, _, _) = await SeedWordsAsync();
        var unitId = await TokenizedPageAsync(1, [new AnalysisSpan(0, 0, 1, hot), new AnalysisSpan(0, 2, 3, hot)],
            pageCount: 3);
        await _store.RecordFailedAttemptAsync((await NextAsync()).Id, 1);
        await TokenizedPageAsync(2, [new AnalysisSpan(0, 0, 1, hot)]);
        await _store.RequestAnalysisAsync([1], 0);
        var id = (await _store.GetUnitAnalysisAsync(unitId))[0].Id;
        await _store.StoreAnalysisAsync("gen", "p1", [new AnalysisAnswer(id, null, null)]);

        Assert.Equal(new ReadingStoreCounts(1, 2, 1, 1, 1), await _store.GetCountsAsync(null));
        Assert.Equal(new ReadingStoreCounts(1, 1, 1, 1, 1), await _store.GetCountsAsync([1]));
        Assert.Equal(new ReadingStoreCounts(0, 1, 0, 0, 0), await _store.GetCountsAsync([2, 2]));
        Assert.Equal(new ReadingStoreCounts(0, 0, 0, 0, 0), await _store.GetCountsAsync([]));
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
            await SaveAsync(unit.Id, Stamp, [Block(0, "x")],
                [new AnalysisSpan(0, 0, 1, words.Hot)]);
            await _store.RequestAnalysisAsync([file], 0);
        })));

        Assert.Equal(new ReadingStoreCounts(80, 20, 0, 20, 0), await _store.GetCountsAsync(null));
        Assert.Equal(24, await ScalarAsync("SELECT COUNT(*) FROM word"));
    }

    #endregion
}
