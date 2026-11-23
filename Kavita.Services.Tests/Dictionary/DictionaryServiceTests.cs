using Kavita.API.Database;
using Kavita.API.Services;
using Kavita.API.Services.SignalR;
using Kavita.Common;
using Kavita.Database;
using Kavita.Database.Tests;
using Kavita.Models.Builders;
using Kavita.Models.DTOs.Dictionary;
using Kavita.Models.DTOs.SignalR;
using Kavita.Models.DTOs.SignalR.Bodies;
using Kavita.Models.Entities;
using Kavita.Models.Entities.Enums;
using Kavita.Services.Builders;
using Kavita.Services.Dictionary;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit.Abstractions;

namespace Kavita.Services.Tests.Dictionary;

public class DictionaryServiceTests(ITestOutputHelper outputHelper) : AbstractDbTest(outputHelper)
{
    private readonly IReadingStore _store = Substitute.For<IReadingStore>();
    private readonly ITokenizeService _tokenizeService = Substitute.For<ITokenizeService>();
    private readonly ITokenizeWorkerSignal _workerSignal = Substitute.For<ITokenizeWorkerSignal>();
    private readonly IEventHub _eventHub = Substitute.For<IEventHub>();

    private static readonly SenseDto SpicySense = new("1001:1", ["adj-i"], ["spicy"]);
    private static readonly SenseDto HarshSense = new("1001:2", ["adj-i"], ["harsh"]);
    private static readonly StoredWord Karai = new(11, "辛い", "形容詞", false, "からい", 3, ["からい", "つらい"],
        [SpicySense, HarshSense], "lex1");
    private static readonly StoredWord Nihon = new(21, "日本", "名詞", false, "にほん", 5, ["にほん", "にっぽん"],
        [new SenseDto("2001:1", ["n"], ["Japan"])], "lex1");
    private static readonly StoredWord Go = new(22, "語", "名詞", false, "ご", 4, ["ご"],
        [new SenseDto("2002:1", ["n"], ["language"])], "lex1");
    private static readonly StoredWord Nihongo = new(20, "日本語", "名詞", false, "にほんご", 5, ["にほんご"],
        [new SenseDto("2000:1", ["n"], ["Japanese language"])], "lex1");

    private sealed record Book(IUnitOfWork UnitOfWork, DataContext Context, DictionaryService Service, Series Series,
        Chapter First, Chapter Second, MangaFile FirstFile, MangaFile SecondFile);

    private async Task<Book> Setup()
    {
        var (unitOfWork, context, _) = await CreateDatabase();

        var firstFile = new MangaFileBuilder("/books/Kokoro/Kokoro 1.epub", MangaFormat.Epub, 10).Build();
        var secondFile = new MangaFileBuilder("/books/Kokoro/Kokoro 2.epub", MangaFormat.Epub, 10).Build();
        var pdf = new MangaFileBuilder("/books/Kokoro/Kokoro notes.pdf", MangaFormat.Pdf, 3).Build();
        var first = new ChapterBuilder("1").WithFile(firstFile).Build();
        var second = new ChapterBuilder("2").WithFile(secondFile).Build();
        var notes = new ChapterBuilder("3").WithFile(pdf).Build();
        var series = new SeriesBuilder("Kokoro")
            .WithFormat(MangaFormat.Epub)
            .WithVolume(new VolumeBuilder("1").WithChapter(first).WithChapter(second).WithChapter(notes).Build())
            .Build();
        context.Library.Add(new LibraryBuilder("Japanese", LibraryType.Book).WithSeries(series).Build());
        await context.SaveChangesAsync();

        var service = new DictionaryService(Substitute.For<ILogger<DictionaryService>>(), unitOfWork, _store,
            _tokenizeService, _workerSignal, _eventHub, new MemoryCache(new MemoryCacheOptions()));
        return new Book(unitOfWork, context, service, series, first, second, firstFile, secondFile);
    }

    private void GivenWords(params StoredWord[] words)
    {
        _store.GetWordsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(call => words
                .Where(w => call.Arg<IReadOnlyCollection<long>>().Contains(w.Id))
                .ToDictionary(w => w.Id));
    }

    private static StoredAnalysis Row(long id, long unitId, int block, int s, int e, long wordId,
        AnalysisState state = AnalysisState.Pending, int priority = ReadingPriority.WholeBook, string? sense = null,
        string? reading = null, string? generator = null)
    {
        return new StoredAnalysis(id, unitId, block, s, e, wordId, state, priority, sense, reading, generator,
            generator == null ? null : "p1");
    }

    #region Page tokens

    /// <summary>
    /// 辛い in block 0, and the compound 日本語 with its parts 日本 and 語 in block 1
    /// </summary>
    private ReadingUnit GivenTokenizedPage(MangaFile file, int page, ReadingUnitState state = ReadingUnitState.Done)
    {
        var unit = new ReadingUnit(5, ReadingUnitKind.Epub, file.Id, page, state, "stamp", "sidecar=1",
        [
            new StoredBlock(0, "とても辛い。", [new TokenDto(0, 3, null, null, null), new TokenDto(3, 5, Karai.Id, "つらい", null),
                new TokenDto(5, 6, null, null, null)]),
            new StoredBlock(1, "日本語だ", [new TokenDto(0, 3, Nihongo.Id, "にほんご",
                [new SubTokenDto(0, 2, Nihon.Id, "にほん"), new SubTokenDto(2, 3, Go.Id, "ご")]),
                new TokenDto(3, 4, null, null, null)]),
        ]);
        _store.GetUnitAsync(file.Id, page, Arg.Any<CancellationToken>()).Returns(unit);
        GivenWords(Karai, Nihongo, Nihon, Go);
        return unit;
    }

    [Fact]
    public async Task GetEpubPage_ReturnsBlocksWordsOfTokensAndPartsAndOnlyDoneAnalysis()
    {
        var book = await Setup();
        var unit = GivenTokenizedPage(book.FirstFile, 2);
        _store.GetUnitAnalysisAsync(unit.Id, Arg.Any<CancellationToken>()).Returns([
            Row(1, unit.Id, 0, 3, 5, Karai.Id, AnalysisState.Done, sense: HarshSense.Key, reading: "つらい", generator: "claude-sonnet"),
            Row(2, unit.Id, 1, 0, 3, Nihongo.Id, AnalysisState.Pending),
            Row(3, unit.Id, 1, 3, 4, Nihongo.Id, AnalysisState.Idle),
        ]);

        var page = await book.Service.GetEpubPageAsync(book.First.Id, 2);

        Assert.NotNull(page);
        Assert.Equal("sidecar=1", page.Version);
        Assert.Equal([0, 1], page.Blocks.Select(b => b.Index));
        Assert.Equal([6, 4], page.Blocks.Select(b => b.Length));
        Assert.Equal(unit.Blocks[1].Tokens, page.Blocks[1].Tokens);
        Assert.Equal([Karai.Id, Nihongo.Id, Nihon.Id, Go.Id], page.Words.Keys.Order());
        Assert.Equal(new WordBriefDto("辛い", "からい", 3), page.Words[Karai.Id]);
        Assert.Equal([new AnalysisEntryDto(0, 3, HarshSense.Key, "つらい")], page.Analysis);
        _workerSignal.DidNotReceive().Wake();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ReadingUnitState.Pending)]
    [InlineData(ReadingUnitState.Failed)]
    public async Task GetEpubPage_IsNullAndRaisesTheUnitWhenThePageIsNotDone(ReadingUnitState? state)
    {
        var book = await Setup();
        if (state != null) GivenTokenizedPage(book.FirstFile, 2, state.Value);

        var page = await book.Service.GetEpubPageAsync(book.First.Id, 2);

        Assert.Null(page);
        await _store.Received(1).RaiseUnitPriorityAsync(book.FirstFile.Id, 2, ReadingPriority.OpenChapter, Arg.Any<CancellationToken>());
        _workerSignal.Received(1).Wake();
    }

    [Fact]
    public async Task GetEpubPage_IsNullForAChapterWithoutAnEpub()
    {
        var book = await Setup();
        var notes = await book.Context.Chapter.SingleAsync(c => c.Range == "3");

        Assert.Null(await book.Service.GetEpubPageAsync(notes.Id, 0));
        await _store.DidNotReceiveWithAnyArgs().GetUnitAsync(default, default, default);
    }

    #endregion

    #region Lookup

    [Fact]
    public async Task Lookup_ReturnsTheWordItsSensesAndTheDoneAnswerAndRaisesTheBlock()
    {
        var book = await Setup();
        var unit = GivenTokenizedPage(book.FirstFile, 2);
        _store.GetUnitAnalysisAsync(unit.Id, Arg.Any<CancellationToken>()).Returns([
            Row(1, unit.Id, 0, 3, 5, Karai.Id, AnalysisState.Done, sense: HarshSense.Key, reading: "つらい", generator: "claude-sonnet"),
        ]);

        var result = await book.Service.LookupAsync(new DictionaryLookupRequestDto(book.First.Id, 2, 0, 3, null));

        Assert.NotNull(result);
        Assert.Equal(new DictionaryWordDto(Karai.Id, "辛い", "辛い", "形容詞", false, "つらい", "からい", ["からい", "つらい"], 3)
            with { Readings = result.Word.Readings }, result.Word);
        Assert.Equal(["からい", "つらい"], result.Word.Readings);
        Assert.Equal([SpicySense, HarshSense], result.Senses);
        Assert.Equal(new DictionaryAnalysisDto(HarshSense.Key, "つらい", "claude-sonnet"), result.Analysis);
        Assert.Empty(result.Parts);
        await _store.Received(1).RaiseAnalysisPriorityAsync(book.FirstFile.Id, 2, 0, ReadingPriority.Tapped, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Lookup_OfACompoundListsItsPartsWithTheirSenses()
    {
        var book = await Setup();
        var unit = GivenTokenizedPage(book.FirstFile, 2);
        _store.GetUnitAnalysisAsync(unit.Id, Arg.Any<CancellationToken>()).Returns([
            Row(2, unit.Id, 1, 0, 3, Nihongo.Id, AnalysisState.Pending),
        ]);

        var result = await book.Service.LookupAsync(new DictionaryLookupRequestDto(book.First.Id, 2, 1, 0, null));

        Assert.NotNull(result);
        Assert.Equal("日本語", result.Word.Surface);
        Assert.Null(result.Analysis);
        Assert.Equal([0, 1], result.Parts.Select(p => p.Sub));
        Assert.Equal(["日本", "語"], result.Parts.Select(p => p.Surface));
        Assert.Equal([Nihon.Id, Go.Id], result.Parts.Select(p => p.WordId));
        Assert.Equal("にほん", result.Parts[0].Reading);
        Assert.Equal(Go.Senses, result.Parts[1].Senses);
    }

    [Fact]
    public async Task Lookup_OfAPartReturnsThePartsWordWithNoAnalysisAndKeepsTheParts()
    {
        var book = await Setup();
        var unit = GivenTokenizedPage(book.FirstFile, 2);
        _store.GetUnitAnalysisAsync(unit.Id, Arg.Any<CancellationToken>()).Returns([
            Row(2, unit.Id, 1, 0, 3, Nihongo.Id, AnalysisState.Done, sense: "2000:1", generator: "claude-sonnet"),
        ]);

        var result = await book.Service.LookupAsync(new DictionaryLookupRequestDto(book.First.Id, 2, 1, 0, 1));

        Assert.NotNull(result);
        Assert.Equal(Go.Id, result.Word.Id);
        Assert.Equal("語", result.Word.Surface);
        Assert.Equal("ご", result.Word.OccurrenceReading);
        Assert.Equal(Go.Senses, result.Senses);
        Assert.Null(result.Analysis);
        Assert.Equal(2, result.Parts.Count);
    }

    [Theory]
    [InlineData(7, 0, null)]
    [InlineData(0, 4, null)]
    [InlineData(0, 0, null)]
    [InlineData(1, 0, 2)]
    [InlineData(1, 0, -1)]
    public async Task Lookup_IsNullForAnUnknownBlockOffsetOrPartOrATokenWithoutAWord(int block, int s, int? sub)
    {
        var book = await Setup();
        GivenTokenizedPage(book.FirstFile, 2);

        Assert.Null(await book.Service.LookupAsync(new DictionaryLookupRequestDto(book.First.Id, 2, block, s, sub)));
    }

    [Fact]
    public async Task Lookup_IsNullWhenThePageIsNotTokenized()
    {
        var book = await Setup();
        GivenTokenizedPage(book.FirstFile, 2, ReadingUnitState.Pending);

        Assert.Null(await book.Service.LookupAsync(new DictionaryLookupRequestDto(book.First.Id, 2, 0, 3, null)));
        await _store.DidNotReceiveWithAnyArgs().RaiseAnalysisPriorityAsync(default, default, default, default, default);
    }

    #endregion

    #region Chapter and series

    private async Task GivenAnalysisOnOpen(IUnitOfWork unitOfWork, DataContext context, bool on)
    {
        var setting = await context.ServerSetting.SingleAsync(s => s.Key == ServerSettingKey.DictionaryAnalysisOnOpen);
        setting.Value = on.ToString().ToLowerInvariant();
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    [Fact]
    public async Task PrioritizeChapter_RequestsAnalysisOfBothChaptersWhenTheSettingIsOn()
    {
        var book = await Setup();
        await GivenAnalysisOnOpen(book.UnitOfWork, book.Context, true);
        _tokenizeService.PrioritizeChapterAsync(book.First.Id, Arg.Any<CancellationToken>())
            .Returns(new PrioritizedFiles([book.FirstFile.Id], [book.SecondFile.Id]));

        await book.Service.PrioritizeChapterAsync(book.First.Id);

        await _store.Received(1).RequestAnalysisAsync(Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { book.FirstFile.Id })),
            ReadingPriority.OpenChapter, Arg.Any<CancellationToken>());
        await _store.Received(1).RequestAnalysisAsync(Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { book.SecondFile.Id })),
            ReadingPriority.NextChapter, Arg.Any<CancellationToken>());
        await _store.DidNotReceiveWithAnyArgs().RaiseAnalysisPriorityAsync(default, default, default, default, default);
    }

    [Fact]
    public async Task PrioritizeChapter_OnlyRaisesPendingRowsWhenTheSettingIsOff()
    {
        var book = await Setup();
        await GivenAnalysisOnOpen(book.UnitOfWork, book.Context, false);
        _tokenizeService.PrioritizeChapterAsync(book.First.Id, Arg.Any<CancellationToken>())
            .Returns(new PrioritizedFiles([book.FirstFile.Id], [book.SecondFile.Id]));

        await book.Service.PrioritizeChapterAsync(book.First.Id);

        await _store.Received(1).RaiseAnalysisPriorityAsync(book.FirstFile.Id, null, null, ReadingPriority.OpenChapter, Arg.Any<CancellationToken>());
        await _store.Received(1).RaiseAnalysisPriorityAsync(book.SecondFile.Id, null, null, ReadingPriority.NextChapter, Arg.Any<CancellationToken>());
        await _store.DidNotReceiveWithAnyArgs().RequestAnalysisAsync(default!, default, default);
    }

    [Fact]
    public async Task PrioritizeChapter_SkipsTheNextChapterAtTheEnd()
    {
        var book = await Setup();
        await GivenAnalysisOnOpen(book.UnitOfWork, book.Context, true);
        _tokenizeService.PrioritizeChapterAsync(book.Second.Id, Arg.Any<CancellationToken>())
            .Returns(new PrioritizedFiles([book.SecondFile.Id], []));

        await book.Service.PrioritizeChapterAsync(book.Second.Id);

        await _store.Received(1).RequestAnalysisAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnalyzeSeries_RequestsEveryEpubOfTheSeriesAtWholeBookPriority()
    {
        var book = await Setup();

        var count = await book.Service.AnalyzeSeriesAsync(book.Series.Id);

        Assert.Equal(2, count);
        await _store.Received(1).RequestAnalysisAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.Order().SequenceEqual(new[] { book.FirstFile.Id, book.SecondFile.Id }.Order())),
            ReadingPriority.WholeBook, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveSeries_ClearsTheAnalysisOfEveryEpubOfTheSeries()
    {
        var book = await Setup();

        await book.Service.RemoveSeriesAsync(book.Series.Id);

        await _store.Received(1).ClearAnalysisAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.Order().SequenceEqual(new[] { book.FirstFile.Id, book.SecondFile.Id }.Order())),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnalyzeSeries_OfASeriesWithoutEpubsRequestsNothing()
    {
        var book = await Setup();

        Assert.Equal(0, await book.Service.AnalyzeSeriesAsync(book.Series.Id + 100));
        await _store.DidNotReceiveWithAnyArgs().RequestAnalysisAsync(default!, default, default);
    }

    #endregion

    #region Pending passages

    /// <summary>
    /// Registers a unit with the store and returns its pending rows as the queue would give them, in row order
    /// </summary>
    private List<PendingAnalysisRow> GivenUnit(long unitId, int fileId, int page, string[] blocks, params StoredAnalysis[] rows)
    {
        var unit = new ReadingUnit(unitId, ReadingUnitKind.Epub, fileId, page, ReadingUnitState.Done, "stamp", "sidecar=1",
            blocks.Select((text, index) => new StoredBlock(index, text, [])).ToList());
        _store.GetUnitAsync(fileId, page, Arg.Any<CancellationToken>()).Returns(unit);
        _store.GetUnitAnalysisAsync(unitId, Arg.Any<CancellationToken>()).Returns(rows);
        return rows
            .Where(r => r.State == AnalysisState.Pending)
            .Select(r => new PendingAnalysisRow(r.Id, unitId, fileId, page, r.Block, r.S, r.E, r.WordId, r.Priority, blocks[r.Block]))
            .ToList();
    }

    private void GivenQueue(IEnumerable<PendingAnalysisRow> rows)
    {
        var queue = rows.ToList();
        _store.GetPendingAnalysisAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => queue
                .Where(r => r.Priority >= call.ArgAt<int>(1))
                .Take(call.ArgAt<int>(0))
                .ToList());
    }

    private static void AssertItemsMatchText(AnalysisPassageDto passage)
    {
        foreach (var item in passage.Items) Assert.Equal(item.Surface, passage.Text[item.S..item.E]);
        Assert.Equal(passage.Items.OrderBy(i => i.S), passage.Items);
        for (var i = 1; i < passage.Items.Count; i++) Assert.True(passage.Items[i].S >= passage.Items[i - 1].E);
    }

    [Fact]
    public async Task GetPendingAnalysis_JoinsBlocksWithANewlineAndShiftsUtf16OffsetsPastSurrogatePairs()
    {
        var book = await Setup();
        GivenWords(Karai, Nihongo);
        // 𠮟 is outside the BMP, so it takes two UTF-16 units and shifts everything after it by two
        string[] blocks = ["前の文。", "𠮟られた彼は辛い", "日本語も辛い"];
        GivenQueue(GivenUnit(5, book.FirstFile.Id, 3, blocks,
            Row(1, 5, 1, 7, 9, Karai.Id),
            Row(2, 5, 2, 0, 3, Nihongo.Id),
            Row(3, 5, 2, 4, 6, Karai.Id)));

        var passages = await book.Service.GetPendingAnalysisAsync(4, 0);

        var passage = Assert.Single(passages);
        Assert.Equal("前の文。\n𠮟られた彼は辛い\n日本語も辛い", passage.Text);
        Assert.Equal([1L, 2L, 3L], passage.Items.Select(i => i.Id));
        Assert.Equal([12, 15, 19], passage.Items.Select(i => i.S));
        Assert.Equal(["辛い", "日本語", "辛い"], passage.Items.Select(i => i.Surface));
        Assert.Equal("辛い", passage.Items[0].Lemma);
        Assert.Equal(Karai.Readings, passage.Items[0].Readings);
        Assert.Equal(Karai.Senses, passage.Items[0].Senses);
        AssertItemsMatchText(passage);
    }

    [Fact]
    public async Task GetPendingAnalysis_LeavesOutRowsBelowMinPriorityAndTrailingBlocksWithoutRows()
    {
        var book = await Setup();
        GivenWords(Karai);
        string[] blocks = ["辛い", "辛い", "辛い"];
        GivenQueue(GivenUnit(5, book.FirstFile.Id, 0, blocks,
            Row(1, 5, 0, 0, 2, Karai.Id, priority: ReadingPriority.Tapped),
            Row(2, 5, 2, 0, 2, Karai.Id, priority: ReadingPriority.WholeBook)));

        var passages = await book.Service.GetPendingAnalysisAsync(4, ReadingPriority.NextChapter);

        var passage = Assert.Single(passages);
        Assert.Equal("辛い", passage.Text);
        Assert.Equal([1L], passage.Items.Select(i => i.Id));
        await _store.Received().GetPendingAnalysisAsync(Arg.Any<int>(), ReadingPriority.NextChapter, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetPendingAnalysis_SplitsAUnitIntoPassagesWithoutSplittingABlocksRows()
    {
        var book = await Setup();
        GivenWords(Karai);
        var block = "辛い" + new string('あ', 898);
        var blocks = Enumerable.Repeat(block, 5).ToArray();
        var rows = Enumerable.Range(0, 5)
            .SelectMany(b => new[] { Row(b * 10 + 1, 5, b, 0, 2, Karai.Id), Row(b * 10 + 2, 5, b, 100, 102, Karai.Id) })
            .ToArray();
        // Only the first row of each block is in the queue, as if the fetch had stopped short of the rest
        GivenQueue(GivenUnit(5, book.FirstFile.Id, 0, blocks, rows).Where(r => r.Id % 10 == 1));

        var passages = await book.Service.GetPendingAnalysisAsync(4, 0);

        Assert.Equal(3, passages.Count);
        Assert.Equal(block + "\n" + block, passages[0].Text);
        Assert.Equal([1L, 2L, 11L, 12L], passages[0].Items.Select(i => i.Id));
        // Block 1 doesn't fit in front of blocks 2 and 3, but block 3 fits in front of block 4 as context
        Assert.Equal(block + "\n" + block, passages[1].Text);
        Assert.Equal([21L, 22L, 31L, 32L], passages[1].Items.Select(i => i.Id));
        Assert.Equal(block + "\n" + block, passages[2].Text);
        Assert.Equal([41L, 42L], passages[2].Items.Select(i => i.Id));
        Assert.Equal(901, passages[2].Items[0].S);
        Assert.Equal(rows.Select(r => r.Id), passages.SelectMany(p => p.Items).Select(i => i.Id));
        foreach (var passage in passages) AssertItemsMatchText(passage);
    }

    [Fact]
    public async Task GetPendingAnalysis_StopsAtTheLimitInQueueOrder()
    {
        var book = await Setup();
        GivenWords(Karai);
        var queue = new List<PendingAnalysisRow>();
        for (var page = 0; page < 5; page++)
        {
            queue.AddRange(GivenUnit(100 + page, book.FirstFile.Id, page, ["辛い"],
                Row(page + 1, 100 + page, 0, 0, 2, Karai.Id, priority: 30 - page)));
        }
        GivenQueue(queue);

        var passages = await book.Service.GetPendingAnalysisAsync(2, 0);

        Assert.Equal([1L, 2L], passages.SelectMany(p => p.Items).Select(i => i.Id));
    }

    [Fact]
    public async Task GetPendingAnalysis_DropsOverlappingRowsAndSortsByOffset()
    {
        var book = await Setup();
        GivenWords(Karai);
        GivenQueue(GivenUnit(5, book.FirstFile.Id, 0, ["辛い辛い"],
            Row(2, 5, 0, 2, 4, Karai.Id),
            Row(1, 5, 0, 0, 2, Karai.Id),
            Row(3, 5, 0, 1, 3, Karai.Id)));

        var passage = Assert.Single(await book.Service.GetPendingAnalysisAsync(4, 0));

        Assert.Equal([1L, 2L], passage.Items.Select(i => i.Id));
        AssertItemsMatchText(passage);
    }

    [Fact]
    public async Task GetPendingAnalysis_ReadsMoreOfTheQueueWhenTheFirstRowsBuildTooFewPassages()
    {
        var book = await Setup();
        GivenWords(Karai);
        // Rows of a unit that's gone build nothing
        var stale = Enumerable.Range(1, 70)
            .Select(i => new PendingAnalysisRow(i, 900, book.FirstFile.Id, 9, 0, 0, 2, Karai.Id, 30, "辛い"))
            .ToList();
        GivenQueue(stale.Concat(GivenUnit(5, book.FirstFile.Id, 0, ["辛い"], Row(500, 5, 0, 0, 2, Karai.Id))));

        var passage = Assert.Single(await book.Service.GetPendingAnalysisAsync(1, 0));

        Assert.Equal(500, passage.Items.Single().Id);
        await _store.Received(1).GetPendingAnalysisAsync(64, 0, Arg.Any<CancellationToken>());
        await _store.Received(1).GetPendingAnalysisAsync(256, 0, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetPendingAnalysis_IsEmptyWhenNothingIsPending()
    {
        var book = await Setup();
        GivenQueue([]);

        Assert.Empty(await book.Service.GetPendingAnalysisAsync(4, 0));
        await _store.Received(1).GetPendingAnalysisAsync(256, 0, Arg.Any<CancellationToken>());
    }

    #endregion

    #region Storing answers

    private static AnalysisStoreRequestDto Answers(params AnalysisAnswerDto[] items) =>
        new() { Generator = "claude-sonnet", PromptVersion = "p1", Items = items };

    [Theory]
    [InlineData("", "p1")]
    [InlineData("claude-sonnet", " ")]
    [InlineData("claude-sonnet", "ppppppppppppppppppppppppppppppppppppppppppppppppppppppppppppppppp")]
    public async Task StoreAnalysis_RefusesAMissingOrLongGeneratorOrPromptVersion(string generator, string promptVersion)
    {
        var book = await Setup();

        await Assert.ThrowsAsync<KavitaException>(() => book.Service.StoreAnalysisAsync(
            new AnalysisStoreRequestDto { Generator = generator, PromptVersion = promptVersion }));
        await _store.DidNotReceiveWithAnyArgs().StoreAnalysisAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task StoreAnalysis_RefusesMoreThan2000Items()
    {
        var book = await Setup();
        var items = Enumerable.Range(1, 2001).Select(i => new AnalysisAnswerDto(i, null, null)).ToArray();

        await Assert.ThrowsAsync<KavitaException>(() => book.Service.StoreAnalysisAsync(Answers(items)));
    }

    [Fact]
    public async Task StoreAnalysis_PassesANullAnswerThroughAndMapsEachRejection()
    {
        var book = await Setup();
        _store.StoreAnalysisAsync("claude-sonnet", "p1", Arg.Any<IReadOnlyList<AnalysisAnswer>>(), Arg.Any<CancellationToken>())
            .Returns(new AnalysisStoreOutcome(1, [
                new AnalysisRejection(2, AnalysisRejectReason.NotPending),
                new AnalysisRejection(3, AnalysisRejectReason.SenseNotOffered),
                new AnalysisRejection(4, AnalysisRejectReason.ReadingNotOffered),
                new AnalysisRejection(1, AnalysisRejectReason.Duplicate),
            ]));

        var result = await book.Service.StoreAnalysisAsync(Answers(
            new AnalysisAnswerDto(1, null, null),
            new AnalysisAnswerDto(2, "1001:1", null),
            new AnalysisAnswerDto(3, "1001:9", null),
            new AnalysisAnswerDto(4, null, "からし"),
            new AnalysisAnswerDto(1, "1001:2", null)));

        Assert.Equal(1, result.Stored);
        Assert.Equal([
            new AnalysisRejectionDto(2, "the row doesn't exist or isn't pending"),
            new AnalysisRejectionDto(3, "sense 1001:9 was not offered"),
            new AnalysisRejectionDto(4, "reading からし was not offered"),
            new AnalysisRejectionDto(1, "the id came earlier in this request, and the earlier answer was kept"),
        ], result.Rejected);
        await _store.Received(1).StoreAnalysisAsync("claude-sonnet", "p1",
            Arg.Is<IReadOnlyList<AnalysisAnswer>>(a => a[0] == new AnalysisAnswer(1, null, null) && a.Count == 5),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StoreAnalysis_TellsReadersWhichPagesGotAnswers()
    {
        var book = await Setup();
        GivenWords(Karai);
        GivenQueue(
            GivenUnit(5, book.FirstFile.Id, 0, ["辛い"], Row(1, 5, 0, 0, 2, Karai.Id))
                .Concat(GivenUnit(6, book.FirstFile.Id, 4, ["辛い"], Row(2, 6, 0, 0, 2, Karai.Id)))
                .Concat(GivenUnit(7, book.SecondFile.Id, 1, ["辛い"], Row(3, 7, 0, 0, 2, Karai.Id)))
                .Concat(GivenUnit(8, book.SecondFile.Id, 2, ["辛い"], Row(4, 8, 0, 0, 2, Karai.Id))));
        await book.Service.GetPendingAnalysisAsync(4, 0);
        _store.StoreAnalysisAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<AnalysisAnswer>>(), Arg.Any<CancellationToken>())
            .Returns(new AnalysisStoreOutcome(3, [new AnalysisRejection(4, AnalysisRejectReason.SenseNotOffered)]));

        await book.Service.StoreAnalysisAsync(Answers(
            new AnalysisAnswerDto(2, "1001:1", null),
            new AnalysisAnswerDto(1, null, null),
            new AnalysisAnswerDto(3, null, "からい"),
            new AnalysisAnswerDto(4, "9:9", null)));

        await _eventHub.Received(1).SendMessageAsync(MessageFactory.DictionaryAnalysisReady,
            Arg.Is<SignalRMessageDto>(m => IsReady(m, book.Series.Id, book.First.Id, 0, 4)), false, Arg.Any<CancellationToken>());
        await _eventHub.Received(1).SendMessageAsync(MessageFactory.DictionaryAnalysisReady,
            Arg.Is<SignalRMessageDto>(m => IsReady(m, book.Series.Id, book.Second.Id, 1)), false, Arg.Any<CancellationToken>());
        await _eventHub.Received(2).SendMessageAsync(Arg.Any<string>(), Arg.Any<SignalRMessageDto>(), Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    private static bool IsReady(SignalRMessageDto message, int seriesId, int chapterId, params int[] pages)
    {
        return message.Body is DictionaryAnalysisReadyEventBodyDto body && body.SeriesId == seriesId &&
               body.ChapterId == chapterId && body.Pages.SequenceEqual(pages);
    }

    #endregion
}
