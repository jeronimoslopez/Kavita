using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
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
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit.Abstractions;

namespace Kavita.Services.Tests.Dictionary;

public class DictionaryServiceTests : AbstractDbTest
{
    private const int UserId = 9;

    private readonly IReadingStore _store = Substitute.For<IReadingStore>();

    public DictionaryServiceTests(ITestOutputHelper outputHelper) : base(outputHelper)
    {
        // No furigana elsewhere and no saved choices unless a test gives some
        _store.GetReadingMarksAsync(Arg.Any<int>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, WordMarkTotals>());
        _store.GetReadingMarksAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<bool>(),
            Arg.Any<CancellationToken>()).Returns(new Dictionary<long, WordMarkTotals>());
        _store.GetReadingChoicesAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<IReadOnlyCollection<long>>(),
            Arg.Any<CancellationToken>()).Returns(new Dictionary<long, string>());
        _store.GetBookWordsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns([]);
    }
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

    private async Task<Book> Setup(bool enableDictionary = true, string firstLanguage = "ja", string secondLanguage = "ja",
        string seriesLanguage = "")
    {
        var (unitOfWork, context, _) = await CreateDatabase();

        var firstFile = new MangaFileBuilder("/books/Kokoro/Kokoro 1.epub", MangaFormat.Epub, 10).Build();
        var secondFile = new MangaFileBuilder("/books/Kokoro/Kokoro 2.epub", MangaFormat.Epub, 10).Build();
        var pdf = new MangaFileBuilder("/books/Kokoro/Kokoro notes.pdf", MangaFormat.Pdf, 3).Build();
        var first = new ChapterBuilder("1").WithFile(firstFile).Build();
        var second = new ChapterBuilder("2").WithFile(secondFile).Build();
        var notes = new ChapterBuilder("3").WithFile(pdf).Build();
        first.Language = firstLanguage;
        second.Language = secondLanguage;
        notes.Language = "ja";
        var series = new SeriesBuilder("Kokoro")
            .WithFormat(MangaFormat.Epub)
            .WithVolume(new VolumeBuilder("1").WithChapter(first).WithChapter(second).WithChapter(notes).Build())
            .Build();
        series.Metadata.Language = seriesLanguage;
        var library = new LibraryBuilder("Japanese", LibraryType.Book).WithSeries(series).Build();
        library.EnableDictionary = enableDictionary;
        context.Library.Add(library);
        await context.SaveChangesAsync();

        var service = new DictionaryService(Substitute.For<ILogger<DictionaryService>>(), unitOfWork, _store,
            _tokenizeService, _workerSignal, _eventHub);
        return new Book(unitOfWork, context, service, series, first, second, firstFile, secondFile);
    }

    private void GivenWords(params StoredWord[] words)
    {
        _store.GetWordsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(call => words
                .Where(w => call.Arg<IReadOnlyCollection<long>>().Contains(w.Id))
                .ToDictionary(w => w.Id));
    }

    /// <param name="book">The totals the chapter's file gives</param>
    /// <param name="series">The totals all the series' files give</param>
    private void GivenMarks(int fileId, WordMarkTotals[] book, WordMarkTotals[] series)
    {
        _store.GetReadingMarksAsync(fileId, Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call => Only(book, call.ArgAt<IReadOnlyCollection<long>>(1)));
        _store.GetReadingMarksAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<bool>(),
            Arg.Any<CancellationToken>()).Returns(call => Only(series, call.ArgAt<IReadOnlyCollection<long>>(1)));
    }

    private static IReadOnlyDictionary<long, WordMarkTotals> Only(WordMarkTotals[] totals, IReadOnlyCollection<long> ids) =>
        totals.Where(t => ids.Contains(t.WordId)).ToDictionary(t => t.WordId);

    private void GivenChoices(Dictionary<long, string> choices)
    {
        _store.GetReadingChoicesAsync(UserId, Arg.Any<int>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(call => choices.Where(c => call.ArgAt<IReadOnlyCollection<long>>(2).Contains(c.Key))
                .ToDictionary(c => c.Key, c => c.Value));
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
        ]);

        var page = await book.Service.GetEpubPageAsync(UserId, book.First.Id, 2);

        Assert.NotNull(page);
        Assert.Equal("sidecar=1", page.Version);
        Assert.Equal([0, 1], page.Blocks.Select(b => b.Index));
        Assert.Equal([6, 4], page.Blocks.Select(b => b.Length));
        Assert.Equal(unit.Blocks[1].Tokens, page.Blocks[1].Tokens);
        Assert.Equal([Karai.Id, Nihongo.Id, Nihon.Id, Go.Id], page.Words.Keys.Order());
        var karai = page.Words[Karai.Id];
        Assert.Equal(("辛い", "からい", 3, "形容詞", false), (karai.Lemma, karai.Reading, karai.Jlpt, karai.Pos, karai.IsProper));
        Assert.Equal(["からい", "つらい"], karai.Readings);
        Assert.Equal([SpicySense, HarshSense], karai.Senses);
        Assert.Equal(Go.Senses, page.Words[Go.Id].Senses);
        Assert.All(page.Blocks, b => Assert.Null(b.Phrases));
        Assert.Equal([new AnalysisEntryDto(0, 3, HarshSense.Key, "つらい", "claude-sonnet")], page.Analysis);
        _workerSignal.DidNotReceive().Wake();
    }

    [Fact]
    public async Task GetEpubPage_CarriesTheBlocksPhrasesInOrderAndTheirWords()
    {
        var book = await Setup();
        GivenPageWithPhrases(book.FirstFile, 2);

        var page = await book.Service.GetEpubPageAsync(UserId, book.First.Id, 2);

        Assert.NotNull(page);
        Assert.Equal([new PhraseDto(0, 4, KiGaSuru.Id), new PhraseDto(1, 4, GaSuru.Id)], page.Blocks[0].Phrases);
        // No token points at either phrase word, so only the phrases bring them in
        Assert.Equal([Ki.Id, Suru.Id, KiGaSuru.Id, GaSuru.Id], page.Words.Keys.Order());
        Assert.Equal(KiGaSuru.Senses, page.Words[KiGaSuru.Id].Senses);
        Assert.Equal("表現", page.Words[KiGaSuru.Id].Pos);
        Assert.Empty(page.Words[GaSuru.Id].Senses);
    }

    [Fact]
    public async Task GetEpubPage_CarriesTheBlocksRubyAsArrays()
    {
        var book = await Setup();
        var unit = GivenTokenizedPage(book.FirstFile, 2);
        var withRuby = unit with
        {
            Blocks = [unit.Blocks[0] with { Ruby = [new RubyDto(3, 4, "つら")] }, unit.Blocks[1]],
        };
        _store.GetUnitAsync(book.FirstFile.Id, 2, Arg.Any<CancellationToken>()).Returns(withRuby);

        var page = await book.Service.GetEpubPageAsync(UserId, book.First.Id, 2);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(page, Web));

        Assert.Equal([new RubyDto(3, 4, "つら")], page!.Blocks[0].Ruby);
        Assert.Null(page.Blocks[1].Ruby);
        var blocks = json.RootElement.GetProperty("blocks");
        Assert.Equal("""[[3,4,"つら"]]""", blocks[0].GetProperty("ruby").GetRawText());
        Assert.False(blocks[1].TryGetProperty("ruby", out _));
    }

    [Fact]
    public async Task GetEpubPage_OfAPageStoredBeforePhrasesWritesNoPhrasesAndOmitsAFalseIsProper()
    {
        var book = await Setup();
        GivenTokenizedPage(book.FirstFile, 2);

        var page = await book.Service.GetEpubPageAsync(UserId, book.First.Id, 2);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(page, Web));

        var blocks = json.RootElement.GetProperty("blocks");
        Assert.All(blocks.EnumerateArray(), b => Assert.False(b.TryGetProperty("phrases", out _)));
        Assert.All(blocks.EnumerateArray(), b => Assert.False(b.TryGetProperty("ruby", out _)));
        Assert.Equal("""[[0,3,20,"にほんご",[[0,2,21,"にほん"],[2,3,22,"ご"]]],[3,4,null,null,null]]""",
            blocks[1].GetProperty("tokens").GetRawText());
        var karai = json.RootElement.GetProperty("words").GetProperty("11");
        Assert.False(karai.TryGetProperty("isProper", out _));
        Assert.Equal(3, karai.GetProperty("jlpt").GetInt32());
        Assert.Equal("""[{"key":"1001:1","pos":["adj-i"],"gloss":["spicy"]},{"key":"1001:2","pos":["adj-i"],"gloss":["harsh"]}]""",
            karai.GetProperty("senses").GetRawText());
    }

    [Fact]
    public async Task GetEpubPageAndLookup_ServeTheOldTokensWhileARefreshWaits()
    {
        var book = await Setup();
        GivenTokenizedPage(book.FirstFile, 2, ReadingUnitState.Refresh);

        var page = await book.Service.GetEpubPageAsync(UserId, book.First.Id, 2);
        var lookup = await book.Service.LookupAsync(UserId, new DictionaryLookupRequestDto(book.First.Id, 2, 0, 3, null));

        Assert.NotNull(page);
        Assert.Equal("sidecar=1", page.Version);
        Assert.Equal(2, page.Blocks.Count);
        Assert.Equal(Karai.Id, lookup?.Word.Id);
        await _store.DidNotReceiveWithAnyArgs().RaiseUnitPriorityAsync(default, default, default, default);
    }

    [Fact]
    public async Task GetEpubPage_IsNullAndRaisesAPendingUnit()
    {
        var book = await Setup();
        GivenTokenizedPage(book.FirstFile, 2, ReadingUnitState.Pending);

        var page = await book.Service.GetEpubPageAsync(UserId, book.First.Id, 2);

        Assert.Null(page);
        await _store.Received(1).RaiseUnitPriorityAsync(book.FirstFile.Id, 2, ReadingPriority.OpenChapter, Arg.Any<CancellationToken>());
        _workerSignal.Received(1).Wake();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ReadingUnitState.Failed)]
    public async Task GetEpubPage_IsNullWithoutAWriteForAMissingOrFailedUnit(ReadingUnitState? state)
    {
        var book = await Setup();
        if (state != null) GivenTokenizedPage(book.FirstFile, 2, state.Value);

        var page = await book.Service.GetEpubPageAsync(UserId, book.First.Id, 2);

        Assert.Null(page);
        await _store.DidNotReceiveWithAnyArgs().RaiseUnitPriorityAsync(default, default, default, default);
        _workerSignal.DidNotReceive().Wake();
    }

    [Fact]
    public async Task GetEpubPage_IsNullForAChapterWithoutAnEpub()
    {
        var book = await Setup();
        var notes = await book.Context.Chapter.SingleAsync(c => c.Range == "3");

        Assert.Null(await book.Service.GetEpubPageAsync(UserId, notes.Id, 0));
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

        var result = await book.Service.LookupAsync(UserId, new DictionaryLookupRequestDto(book.First.Id, 2, 0, 3, null));

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

        var result = await book.Service.LookupAsync(UserId, new DictionaryLookupRequestDto(book.First.Id, 2, 1, 0, null));

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

        var result = await book.Service.LookupAsync(UserId, new DictionaryLookupRequestDto(book.First.Id, 2, 1, 0, 1));

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

        Assert.Null(await book.Service.LookupAsync(UserId, new DictionaryLookupRequestDto(book.First.Id, 2, block, s, sub)));
    }

    private static readonly StoredWord Ki = new(31, "気", "名詞", false, "き", 4, ["き"],
        [new SenseDto("3001:1", ["n"], ["spirit", "mind"])], "lex1");
    private static readonly StoredWord Suru = new(32, "する", "動詞", false, "する", 5, ["する"],
        [new SenseDto("3002:1", ["vs-i"], ["to do"])], "lex1");
    private static readonly StoredWord KiGaSuru = new(33, "気がする", "表現", false, "きがする", null, ["きがする"],
        [new SenseDto("3003:1", ["exp"], ["to have a feeling that", "to feel like"]), new SenseDto("3003:2", ["exp"], ["to sense"])],
        "lex1");
    private static readonly StoredWord GaSuru = new(34, "がする", "表現", false, "がする", null, ["がする"], [], "lex1");

    /// <summary>
    /// 気がした。 as 気, が, the compound した with its parts し and た, and 。. Phrase 0 is 気がする over 気 to した, and
    /// phrase 1 is がする over が and した
    /// </summary>
    private ReadingUnit GivenPageWithPhrases(MangaFile file, int page)
    {
        var block = new StoredBlock(0, "気がした。",
        [
            new TokenDto(0, 1, Ki.Id, "き", null), new TokenDto(1, 2, null, null, null),
            new TokenDto(2, 4, Suru.Id, "した", [new SubTokenDto(2, 3, Suru.Id, "し"), new SubTokenDto(3, 4, null, null)]),
            new TokenDto(4, 5, null, null, null),
        ]) { Phrases = [new PhraseDto(0, 4, KiGaSuru.Id), new PhraseDto(1, 4, GaSuru.Id)] };
        var unit = new ReadingUnit(6, ReadingUnitKind.Epub, file.Id, page, ReadingUnitState.Done, "stamp", "sidecar=2", [block]);
        _store.GetUnitAsync(file.Id, page, Arg.Any<CancellationToken>()).Returns(unit);
        GivenWords(Ki, Suru, KiGaSuru, GaSuru);
        return unit;
    }

    [Theory]
    [InlineData(0, null, new[] { 0 })]
    [InlineData(2, null, new[] { 0, 1 })]
    [InlineData(2, 0, new[] { 0, 1 })] // a part lists the phrases of its token
    public async Task Lookup_ListsThePhrasesThatCoverTheToken(int s, int? sub, int[] expected)
    {
        var book = await Setup();
        GivenPageWithPhrases(book.FirstFile, 2);

        var result = await book.Service.LookupAsync(UserId, new DictionaryLookupRequestDto(book.First.Id, 2, 0, s, sub));

        Assert.NotNull(result);
        Assert.Equal(expected, result.PartOf.Select(p => p.Index));
        Assert.Equal(new DictionaryPhraseDto(0, 0, 4, "気がする", "きがする", "to have a feeling that; to feel like"),
            result.PartOf[0]);
        if (expected.Length > 1) Assert.Equal(string.Empty, result.PartOf[1].Gloss);
    }

    [Fact]
    public async Task Lookup_OfATokenInNoPhraseHasNoPartOf()
    {
        var book = await Setup();
        GivenTokenizedPage(book.FirstFile, 2);

        var result = await book.Service.LookupAsync(UserId, new DictionaryLookupRequestDto(book.First.Id, 2, 0, 3, null));

        Assert.NotNull(result);
        Assert.Empty(result.PartOf);
    }

    [Fact]
    public async Task Lookup_OfAPhraseReturnsItsWordWithNoAnalysisPartsOrPartOf()
    {
        var book = await Setup();
        var unit = GivenPageWithPhrases(book.FirstFile, 2);
        _store.GetUnitAnalysisAsync(unit.Id, Arg.Any<CancellationToken>()).Returns([
            Row(1, unit.Id, 0, 2, 4, Suru.Id, AnalysisState.Done, sense: "3002:1", generator: "claude-sonnet"),
        ]);

        var result = await book.Service.LookupAsync(UserId, new DictionaryLookupRequestDto(book.First.Id, 2, 0, 2, null, 0));

        Assert.NotNull(result);
        Assert.Equal(new DictionaryWordDto(KiGaSuru.Id, "気がした", "気がする", "表現", false, null, "きがする", ["きがする"], null)
            with { Readings = result.Word.Readings }, result.Word);
        Assert.Equal(KiGaSuru.Senses, result.Senses);
        Assert.Null(result.Analysis);
        Assert.Empty(result.Parts);
        Assert.Empty(result.PartOf);
        await _store.Received(1).RaiseAnalysisPriorityAsync(book.FirstFile.Id, 2, 0, ReadingPriority.Tapped, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(2, 2)] // past the block's phrases
    [InlineData(-1, 2)]
    [InlineData(1, 0)] // phrase 1 starts at 1
    [InlineData(0, 4)] // phrase 0 ends at 4
    public async Task Lookup_OfAPhraseIsNullForAnUnknownIndexOrAnOffsetOutsideIt(int phrase, int s)
    {
        var book = await Setup();
        GivenPageWithPhrases(book.FirstFile, 2);

        Assert.Null(await book.Service.LookupAsync(UserId, new DictionaryLookupRequestDto(book.First.Id, 2, 0, s, null, phrase)));
    }

    [Fact]
    public async Task Lookup_IsNullWhenThePageIsNotTokenized()
    {
        var book = await Setup();
        GivenTokenizedPage(book.FirstFile, 2, ReadingUnitState.Pending);

        Assert.Null(await book.Service.LookupAsync(UserId, new DictionaryLookupRequestDto(book.First.Id, 2, 0, 3, null)));
        await _store.DidNotReceiveWithAnyArgs().RaiseAnalysisPriorityAsync(default, default, default, default, default);
    }

    #endregion

    #region Lookup parity fixture

    /// <summary>
    /// The web defaults ASP.NET writes answers with. Non-ASCII stays unescaped so the fixture reads as Japanese,
    /// which changes the bytes and not the values
    /// </summary>
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Read by the reader's local-lookup.spec.ts, which checks that its lookups give these answers
    /// </summary>
    private static readonly string ParityFixturePath = Path.Join(Directory.GetCurrentDirectory(),
        "../../../../UI/Web/src/app/reader-shared/dictionary/fixtures/lookup-parity.json");

    private static readonly StoredWord Totemo = new(41, "とても", "副詞", false, "とても", 4, ["とても"],
        [new SenseDto("4001:1", ["adv"], ["very", "awfully"])], "lex1");
    private static readonly StoredWord Kamisama = new(80, "神様", "名詞", false, "かみさま", null, ["かみさま"],
        [new SenseDto("8000:1", ["n"], ["God"]), new SenseDto("8000:2", ["n"], ["superior person"])], "lex1");
    private static readonly StoredWord Kami = new(81, "神", "名詞", false, "かみ", 4, ["かみ", "しん"],
        [new SenseDto("8001:1", ["n"], ["god", "deity"])], "lex1");
    private static readonly StoredWord Shikaru = new(60, "𠮟る", "動詞", false, "しかる", 2, ["しかる"],
        [new SenseDto("6000:1", ["v5r", "vt"], ["to scold"])], "lex1");
    private static readonly StoredWord Tanaka = new(70, "田中", "名詞", true, "たなか", null, ["たなか"],
        [new SenseDto("7000:1", ["n-pr"], ["Tanaka"])], "lex1");
    private static SenseDto Sense(string key, string gloss, params string[] r) =>
        new(key, ["n"], [gloss], r.Length == 0 ? null : r);

    /// <summary>
    /// Three entries with a sense each, as the sidecar gives 瞬く, with their Jiten ranks where the list has one
    /// </summary>
    private static readonly StoredWord Blink = new(90, "瞬く", "動詞", false, "しばたたく", null,
        ["しばたたく", "しばたく", "またたく", "まばたく", "めたたく", "めばたく"],
        [Sense("2831002:1", "to blink"), Sense("1341200:1", "to twinkle"), Sense("2831001:1", "to wink")], "lex1")
    {
        Entries = [new WordEntryDto(2831002, false, ["しばたたく", "しばたく"], 80172),
            new WordEntryDto(1341200, true, ["またたく", "まばたく"], 8110), new WordEntryDto(2831001, false, ["めたたく", "めばたく"])],
    };
    private static readonly StoredWord Hiraku = new(91, "開く", "動詞", false, "ひらく", 5, ["ひらく", "あく"],
        [Sense("1202440:1", "to open (a door)"), Sense("1586270:1", "to be open")], "lex1")
    {
        Entries = [new WordEntryDto(1202440, true, ["ひらく"]), new WordEntryDto(1586270, true, ["あく"])],
    };
    private static readonly StoredWord Hoshi = new(92, "星", "名詞", false, "ほし", 5, ["ほし", "せい"],
        [Sense("1376310:1", "star"), Sense("2100980:1", "star (counter)")], "lex1")
    {
        Entries = [new WordEntryDto(1376310, true, ["ほし"]), new WordEntryDto(2100980, false, ["せい"])],
    };
    /// <summary>
    /// じょうず, and an entry whose senses JMdict splits between うわて and かみて, so three groups
    /// </summary>
    private static readonly StoredWord Jouzu = new(93, "上手", "名詞", false, "じょうず", 4, ["じょうず", "うわて", "かみて"],
        [Sense("1353320:1", "skilful"), Sense("1580400:1", "upper part"), Sense("1580400:2", "upstage", "かみて"),
            Sense("1580400:3", "superior", "うわて")], "lex1")
    {
        Entries = [new WordEntryDto(1353320, true, ["じょうず"]), new WordEntryDto(1580400, false, ["うわて", "かみて"])],
    };
    /// <summary>
    /// One entry whose second sense is restricted to こんにち, so two groups
    /// </summary>
    private static readonly StoredWord Kyou = new(94, "今日", "名詞", false, "きょう", 5, ["きょう", "こんにち", "こんち"],
        [Sense("1579110:1", "today"), Sense("1579110:2", "these days", "こんにち")], "lex1")
    {
        Entries = [new WordEntryDto(1579110, true, ["きょう", "こんにち", "こんち"])],
    };
    private static readonly StoredWord Shoujo = new(95, "小姐", "名詞", false, "しょうじょ", null, ["しょうじょ"],
        [Sense("9100:1", "young lady")], "lex1") { Entries = [new WordEntryDto(9100, false, ["しょうじょ"])] };
    private static readonly StoredWord Iu = new(96, "言う", "動詞", false, "いう", 5, ["いう", "ゆう"],
        [Sense("1587040:1", "to say")], "lex1") { Entries = [new WordEntryDto(1587040, true, ["いう", "ゆう"])] };
    /// <summary>
    /// A name the dictionary has no entry for, from a version 3 sidecar, so its entries are empty and not missing
    /// </summary>
    private static readonly StoredWord Gaoshun = new(97, "高順", "名詞", true, "たかじゅん", null, [], [], "lex1") { Entries = [] };
    private static readonly StoredWord Midori = new(82, "翠", "名詞", false, "みどり", null, ["みどり"], [Sense("9200:1", "green")], "lex1")
    {
        Entries = [new WordEntryDto(9200, false, ["みどり"])],
    };
    private static readonly StoredWord Rei = new(83, "苓", "名詞", false, "れい", null, [], [], "lex1") { Entries = [] };
    private static readonly StoredWord Uma = new(84, "馬", "名詞", false, "うま", 5, ["うま"], [Sense("9300:1", "horse")], "lex1")
    {
        Entries = [new WordEntryDto(9300, true, ["うま"])],
    };
    /// <summary>
    /// Words the book's furigana defines, with the word rows the reading store gives them
    /// </summary>
    private static readonly StoredWord Suirei = new(98, "翠苓", BookWords.Pos, true, "すいれい", null, [], [], BookWords.LexiconVersion)
    {
        Entries = [],
    };
    private static readonly StoredWord Basen = new(99, "馬閃", BookWords.Pos, true, "ばせん", null, [], [], BookWords.LexiconVersion)
    {
        Entries = [],
    };
    /// <summary>
    /// A kana word whose three entries are all read そう, so one group
    /// </summary>
    private static readonly StoredWord Sou = new(100, "そう", "副詞", false, "そう", 5, ["そう"],
        [Sense("1006610:1", "so, really"), Sense("2137720:1", "I hear"), Sense("2833030:1", "appearing")], "lex1")
    {
        Entries = [new WordEntryDto(1006610, true, ["そう"]), new WordEntryDto(2137720, false, ["そう"]),
            new WordEntryDto(2833030, false, ["そう"])],
    };
    /// <summary>
    /// A kana word whose first entry is 一寸 read いっすん, so one group read ちょ
    /// </summary>
    private static readonly StoredWord Cho = new(101, "ちょ", "副詞", false, "ちょ", null, ["いっすん", "ちょっと", "ちょと"],
        [Sense("1160790:1", "one sun"), Sense("1008950:1", "a little")], "lex1")
    {
        Entries = [new WordEntryDto(1160790, false, ["いっすん"]), new WordEntryDto(1008950, true, ["ちょっと", "ちょと"])],
    };
    /// <summary>
    /// Two entries read こと, which merge, and one read じ
    /// </summary>
    private static readonly StoredWord Koto = new(102, "事", "名詞", false, "こと", 5, ["こと", "じ"],
        [Sense("1313580:1", "thing, matter"), Sense("2827864:1", "fact"), Sense("1313590:1", "affair")], "lex1")
    {
        Entries = [new WordEntryDto(1313580, true, ["こと"]), new WordEntryDto(2827864, false, ["こと"]),
            new WordEntryDto(1313590, false, ["じ"])],
    };
    /// <summary>
    /// Two entries read とき and つき, neither of them common
    /// </summary>
    private static readonly StoredWord Toki = new(103, "鴇", "名詞", false, "とき", null, ["とき", "つき"],
        [Sense("9400:1", "crested ibis"), Sense("9401:1", "ibis (rare)")], "lex1")
    {
        Entries = [new WordEntryDto(9400, false, ["とき"]), new WordEntryDto(9401, false, ["つき"])],
    };
    /// <summary>
    /// 本 read ほん in a common entry, and the counter's ぽん in one that isn't
    /// </summary>
    private static readonly StoredWord Hon = new(104, "本", "名詞", false, "ほん", 5, ["ほん", "ぽん"],
        [Sense("1522150:1", "book"), Sense("9500:1", "counter for long objects")], "lex1")
    {
        Entries = [new WordEntryDto(1522150, true, ["ほん"]), new WordEntryDto(9500, false, ["ぽん"])],
    };
    /// <summary>
    /// 山 as the tokenizer's suffix, read やま in a common entry and さん in one that isn't
    /// </summary>
    private static readonly StoredWord Yama = new(105, "山", "接尾辞", false, "さん", 5, ["やま", "さん"],
        [Sense("1586120:1", "mountain"), Sense("9600:1", "Mt.")], "lex1")
    {
        Entries = [new WordEntryDto(1586120, true, ["やま"]), new WordEntryDto(9600, false, ["さん"])],
    };
    /// <summary>
    /// 杯 read さかずき in a common entry, and the counter's plain はい in one that isn't. No entry has the voiced ばい
    /// </summary>
    private static readonly StoredWord Hai = new(106, "杯", "名詞", false, "さかずき", 5, ["さかずき", "さかづき", "はい"],
        [Sense("1325220:1", "sake cup"), Sense("9700:1", "counter for cupfuls")], "lex1")
    {
        Entries = [new WordEntryDto(1325220, true, ["さかずき", "さかづき"]), new WordEntryDto(9700, false, ["はい"])],
    };

    /// <summary>
    /// 潜る read もぐる in an entry JMdict doesn't mark common, ranked 5055, and くぐる in a common one, ranked 83217
    /// </summary>
    private static readonly StoredWord Dive = new(107, "潜る", "動詞", false, "もぐる", null, ["もぐる", "くぐる"],
        [Sense("1385260:1", "to dive"), Sense("1385270:1", "to pass under")], "lex1")
    {
        Entries = [new WordEntryDto(1385260, false, ["もぐる"], 5055), new WordEntryDto(1385270, true, ["くぐる"], 83217)],
    };
    /// <summary>
    /// The same two entries with くぐる first, as another part of speech
    /// </summary>
    private static readonly StoredWord DiveUnder = Dive with
    {
        Id = 108, Pos = "名詞", Reading = "くぐる", Readings = ["くぐる", "もぐる"],
        Senses = [Dive.Senses[1], Dive.Senses[0]], Entries = [Dive.Entries![1], Dive.Entries[0]],
    };
    /// <summary>
    /// 分 read ぶん in a common entry ranked 300 and ふん in one that isn't, ranked 900, within five times of each other
    /// </summary>
    private static readonly StoredWord Minute = new(109, "分", "名詞", false, "ぶん", 5, ["ぶん", "ふん"],
        [Sense("1502840:1", "part"), Sense("1502850:1", "minute")], "lex1")
    {
        Entries = [new WordEntryDto(1502840, true, ["ぶん"], 300), new WordEntryDto(1502850, false, ["ふん"], 900)],
    };

    /// <summary>
    /// The opened file defines 翠苓, and another file of the series defines 馬閃
    /// </summary>
    private void GivenBookWords(MangaFile file)
    {
        _store.GetBookWordsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns(
        [
            new BookWordTotal(file.Id, Suirei.Lemma, Suirei.Id, "すいれい", "スイレイ", 1),
            new BookWordTotal(file.Id + 1000, Suirei.Lemma, Suirei.Id, "みどりれい", "ミドリレイ", 5),
            new BookWordTotal(file.Id + 1000, Basen.Lemma, Basen.Id, "ばせん", "バセン", 2),
        ]);
    }

    /// <summary>
    /// Word ids that tokens and phrases point at and the store doesn't have
    /// </summary>
    private const long MissingTokenWord = 888;
    private const long MissingPhraseWord = 999;

    /// <summary>
    /// One page with every kind of tap the reader can send
    /// </summary>
    private ReadingUnit GivenParityPage(MangaFile file, int page)
    {
        var unit = new ReadingUnit(7, ReadingUnitKind.Epub, file.Id, page, ReadingUnitState.Done, "stamp", "sidecar=2",
        [
            // A plain word, then 辛かった inflected from 辛い, then punctuation
            new StoredBlock(0, "とても辛かった。",
            [
                new TokenDto(0, 3, Totemo.Id, "とても", null), new TokenDto(3, 7, Karai.Id, "からかった", null),
                new TokenDto(7, 8, null, null, null),
            ]),
            // The compound 日本語, a particle without a word, and 神様 joined from 神 and a part without a word
            new StoredBlock(1, "日本語と神様",
            [
                new TokenDto(0, 3, Nihongo.Id, "にほんご", [new SubTokenDto(0, 2, Nihon.Id, "にほん"), new SubTokenDto(2, 3, Go.Id, "ご")]),
                new TokenDto(3, 4, null, null, null),
                new TokenDto(4, 6, Kamisama.Id, "かみさま", [new SubTokenDto(4, 5, Kami.Id, "かみ"), new SubTokenDto(5, 6, null, "さま")]),
            ]),
            // Phrase 2's word is missing from the store, so lookups leave it out
            new StoredBlock(2, "気がした。",
            [
                new TokenDto(0, 1, Ki.Id, "き", null), new TokenDto(1, 2, null, null, null),
                new TokenDto(2, 4, Suru.Id, "した", [new SubTokenDto(2, 3, Suru.Id, "し"), new SubTokenDto(3, 4, null, null)]),
                new TokenDto(4, 5, null, null, null),
            ]) { Phrases = [new PhraseDto(0, 4, KiGaSuru.Id), new PhraseDto(1, 4, GaSuru.Id), new PhraseDto(0, 2, MissingPhraseWord)] },
            // 𠮟 is a surrogate pair, so 𠮟る is three UTF-16 units. 田中 is a proper noun. Both are stored without
            // entries, so the furigana over them changes no answer
            new StoredBlock(3, "𠮟る田中",
                [new TokenDto(0, 3, Shikaru.Id, "しかる", null), new TokenDto(3, 5, Tanaka.Id, "たなか", null)])
                { Ruby = [new RubyDto(0, 2, "しか"), new RubyDto(3, 5, "タナカ")] },
            new StoredBlock(4, "謎だ", [new TokenDto(0, 1, MissingTokenWord, "なぞ", null), new TokenDto(1, 2, null, null, null)]),
            // 星 has a series hint, and 瞬く is read by fixed cost alone
            new StoredBlock(5, "星が瞬く",
                [new TokenDto(0, 1, Hoshi.Id, "ほし", null), new TokenDto(1, 2, null, null, null), new TokenDto(2, 4, Blink.Id, "しばたたく", null)])
                { Blind = [2] },
            // 開く has a book hint
            new StoredBlock(6, "店を開く",
                [new TokenDto(0, 1, null, null, null), new TokenDto(1, 2, null, null, null), new TokenDto(2, 4, Hiraku.Id, "ひらく", null)]),
            // 上手 has the user's saved choice, and 言っ is one group read in dictionary form
            new StoredBlock(7, "上手に言った",
            [
                new TokenDto(0, 2, Jouzu.Id, "じょうず", null), new TokenDto(2, 3, null, null, null),
                new TokenDto(3, 5, Iu.Id, "いっ", null), new TokenDto(5, 6, null, null, null),
            ]),
            // The second 今日 is marked with a reading one of its groups has
            new StoredBlock(8, "今日と今日",
                [new TokenDto(0, 2, Kyou.Id, "きょう", null), new TokenDto(2, 3, null, null, null), new TokenDto(3, 5, Kyou.Id, "きょう", null)])
                { Ruby = [new RubyDto(3, 5, "こんにち")] },
            // 小姐 is marked with a reading none of its groups has, and the name 高順 has a hint from the book
            new StoredBlock(9, "小姐と高順",
                [new TokenDto(0, 2, Shoujo.Id, "しょうじょ", null), new TokenDto(2, 3, null, null, null), new TokenDto(3, 5, Gaoshun.Id, "たかじゅん", null)])
                { Ruby = [new RubyDto(0, 2, "ねえちゃん")] },
            // A compound without a word of its own whose parts have groups
            new StoredBlock(10, "瞬き星",
                [new TokenDto(0, 3, null, null, [new SubTokenDto(0, 2, Blink.Id, "またたき"), new SubTokenDto(2, 3, Hoshi.Id, "ぼし")])]),
            // The book word 翠苓, split by the tokenizer, marked once and then unmarked. The page response joins both,
            // drops the blind offset inside the first, and drops the analysis row of 翠 alone
            new StoredBlock(11, "翠苓は翠苓と",
            [
                new TokenDto(0, 1, Midori.Id, "みどり", null), new TokenDto(1, 2, Rei.Id, null, null), new TokenDto(2, 3, null, null, null),
                new TokenDto(3, 4, Midori.Id, "みどり", null), new TokenDto(4, 5, Rei.Id, null, null), new TokenDto(5, 6, null, null, null),
            ]) { Ruby = [new RubyDto(0, 2, "スイレイ")], Blind = [0] },
            // The book word 馬閃, defined only by another file of the series, with a part that has no word
            new StoredBlock(12, "馬閃が",
                [new TokenDto(0, 1, Uma.Id, "うま", null), new TokenDto(1, 2, null, "せん", null), new TokenDto(2, 3, null, null, null)]),
            // Kana words whose entries give one group, and a kanji word whose entries read こと merge
            new StoredBlock(13, "そうだ、ちょ事",
            [
                new TokenDto(0, 2, Sou.Id, "そう", null), new TokenDto(2, 3, null, null, null), new TokenDto(3, 4, null, null, null),
                new TokenDto(4, 6, Cho.Id, "ちょ", null), new TokenDto(6, 7, Koto.Id, "こと", null),
            ]),
            // 今日 marked one kanji at a time in one ruby element, so its spans carry a group
            new StoredBlock(14, "今日だ",
                [new TokenDto(0, 2, Kyou.Id, "きょう", null), new TokenDto(2, 3, null, null, null)])
                { Ruby = [new RubyDto(0, 1, "こん", 0), new RubyDto(1, 2, "にち", 0)] },
            // 今日 read by fixed cost in a common group, and 鴇 read by fixed cost where no group is common
            new StoredBlock(15, "今日の鴇",
                [new TokenDto(0, 2, Kyou.Id, "こんにち", null), new TokenDto(2, 3, null, null, null), new TokenDto(3, 4, Toki.Id, "つき", null)])
                { Blind = [0, 3] },
            // 本 after a numeral and the suffix 山, both read by fixed cost in a group that isn't common
            new StoredBlock(16, "二本と富士山",
            [
                new TokenDto(0, 1, null, null, null), new TokenDto(1, 2, Hon.Id, "ぽん", null), new TokenDto(2, 3, null, null, null),
                new TokenDto(3, 5, null, null, null), new TokenDto(5, 6, Yama.Id, "さん", null),
            ]) { Blind = [1, 5] },
            // 杯 after a numeral read by fixed cost as the voiced ばい, which only its plain はい group matches
            new StoredBlock(17, "二杯", [new TokenDto(0, 1, null, null, null), new TokenDto(1, 2, Hai.Id, "ばい", null)])
                { Blind = [1] },
            // 潜る read by fixed cost as もぐる and then as くぐる, where frequency favours もぐる
            new StoredBlock(18, "潜ると潜る",
                [new TokenDto(0, 2, Dive.Id, "もぐる", null), new TokenDto(2, 3, null, null, null), new TokenDto(3, 5, Dive.Id, "くぐる", null)])
                { Blind = [0, 3] },
            // 潜る read せんる, which no group has, with the frequent もぐる group second
            new StoredBlock(19, "潜る", [new TokenDto(0, 2, DiveUnder.Id, "せんる", null)]),
            // 分 read by fixed cost as the rare ふん, ranked too close to ぶん for frequency to decide
            new StoredBlock(20, "分だ", [new TokenDto(0, 1, Minute.Id, "ふん", null), new TokenDto(1, 2, null, null, null)])
                { Blind = [0] },
        ]);
        _store.GetUnitAsync(file.Id, page, Arg.Any<CancellationToken>()).Returns(unit);
        GivenWords(Totemo, Karai, Nihongo, Nihon, Go, Kamisama, Kami, Ki, Suru, KiGaSuru, GaSuru, Shikaru, Tanaka,
            Blink, Hiraku, Hoshi, Jouzu, Kyou, Shoujo, Iu, Gaoshun, Midori, Rei, Uma, Suirei, Basen, Sou, Cho, Koto, Toki, Hon,
            Yama, Hai, Dive, DiveUnder, Minute);
        GivenBookWords(file);
        GivenMarks(file.Id,
            book: [new WordMarkTotals(Hiraku.Id, 4, [new ReadingMarkTotal("あく", "あく", 1)]),
                new WordMarkTotals(Gaoshun.Id, 3, [new ReadingMarkTotal("がおしゅん", "ガオシュン", 1)]),
                new WordMarkTotals(Tanaka.Id, 1, [new ReadingMarkTotal("たなか", "タナカ", 1)]),
                new WordMarkTotals(Jouzu.Id, 5, [new ReadingMarkTotal("じょうず", "じょうず", 2)])],
            series: [new WordMarkTotals(Hoshi.Id, 10, [new ReadingMarkTotal("せい", "せい", 3)]),
                new WordMarkTotals(Hiraku.Id, 40, [new ReadingMarkTotal("ひらく", "ひらく", 30)])]);
        GivenChoices(new Dictionary<long, string> { [Jouzu.Id] = "うわて" });
        _store.GetUnitAnalysisAsync(unit.Id, Arg.Any<CancellationToken>()).Returns([
            Row(1, unit.Id, 0, 3, 7, Karai.Id, AnalysisState.Done, sense: HarshSense.Key, reading: "つらい", generator: "claude-sonnet"),
            Row(2, unit.Id, 0, 0, 3, Totemo.Id, AnalysisState.Pending),
            Row(3, unit.Id, 1, 4, 6, Kamisama.Id, AnalysisState.Done, generator: "claude-haiku"),
            Row(4, unit.Id, 2, 2, 4, Suru.Id, AnalysisState.Done, sense: "3002:1", generator: "claude-sonnet"),
            Row(5, unit.Id, 11, 0, 1, Midori.Id, AnalysisState.Done, sense: "9200:1", generator: "claude-sonnet"),
        ]);
        return unit;
    }

    /// <summary>
    /// Taps as (block, s, sub, phrase). The comment on each says what the server answers
    /// </summary>
    private static readonly (int Block, int S, int? Sub, int? Phrase)[] ParityTaps =
    [
        (0, 0, null, null), // a plain word
        (0, 3, null, null), // an inflected word with its analysis
        (0, 3, 0, null), // null, the token has no parts
        (0, 7, null, null), // null, punctuation has no word
        (0, 1, null, null), // null, no token starts there
        (99, 0, null, null), // null, no such block
        (1, 0, null, null), // a compound with its parts
        (1, 0, 0, null), // a part, keeping the parts
        (1, 0, 1, null),
        (1, 0, 2, null), // null, past the parts
        (1, 0, -1, null), // null
        (1, 4, null, null), // a joined token whose analysis has no sense or reading
        (1, 4, 0, null), // its part with a word
        (1, 4, 1, null), // null, its part without a word
        (2, 0, null, null), // a word inside one phrase whose word exists
        (2, 2, null, null), // a word inside two phrases, with its analysis
        (2, 2, 0, null), // a part inside two phrases, with no analysis
        (2, 1, null, null), // null, inside phrases but without a word
        (2, 2, null, 0), // a phrase
        (2, 1, null, 1), // a phrase whose word has no senses
        (2, 3, 1, 0), // a phrase, which ignores sub and needs no token at s
        (2, 0, null, 1), // null, before the phrase
        (2, 4, null, 0), // null, at the phrase's end
        (2, 0, null, 2), // null, the phrase's word is missing
        (2, 0, null, 3), // null, past the phrases
        (2, 0, null, -1), // null
        (3, 0, null, null), // a surface with a surrogate pair
        (3, 3, null, null), // a proper noun
        (4, 0, null, null), // null, the token's word is missing
        (5, 0, null, null), // a series hint
        (5, 2, null, null), // a blind token in a rare group, so a guess on the common group
        (6, 2, null, null), // a book hint, which the series' other reading doesn't override
        (7, 0, null, null), // the user's saved choice, which beats a book hint
        (7, 3, null, null), // one group, read in dictionary form
        (8, 0, null, null), // groups split by restricted senses, by the tokenizer
        (8, 3, null, null), // furigana that one group has
        (9, 0, null, null), // furigana no group has
        (9, 3, null, null), // a name with empty entries and a book hint
        (10, 0, 0, null), // a part with groups, by its own reading
        (10, 0, 1, null), // a part whose reading no group has
        (11, 0, null, null), // a book word on its furigana, with no groups, and no analysis
        (11, 3, null, null), // a book word unmarked, by the book's hint
        (11, 3, 0, null), // a piece of a book word, with its groups
        (11, 3, 1, null), // a piece with no entries
        (11, 1, null, null), // null, inside a joined token
        (12, 0, null, null), // a book word only the series defines, by the series' hint
        (12, 0, 1, null), // null, a piece without a word
        (13, 0, null, null), // a kana word whose entries all read そう, one group
        (13, 4, null, null), // a kana word whose first entry reads いっすん, one group read ちょ
        (13, 6, null, null), // a kanji word whose こと entries merge, two groups
        (14, 0, null, null), // furigana from one ruby element's spans, read together
        (15, 0, null, null), // a blind token in a common group, a guess from the tokenizer
        (15, 3, null, null), // a blind token where no group is common, a guess from the tokenizer
        (16, 1, null, null), // a blind counter after a numeral in a rare group, a guess from the tokenizer
        (16, 5, null, null), // a blind suffix in a rare group, a guess from the tokenizer
        (17, 1, null, null), // a blind voiced counter after a numeral, a guess on its plain reading's group
        (18, 0, null, null), // a blind token in the group frequency favours, a guess from the tokenizer
        (18, 3, null, null), // a blind token in a common group frequency doesn't favour, a guess on the frequent group
        (19, 0, null, null), // a token reading no group has, a guess on the frequent group
        (20, 0, null, null), // a blind token in a rare group with ranks too close, a guess on the common group
    ];

    /// <summary>
    /// Builds the page response and the lookup answer for every tap in <see cref="ParityTaps"/>, and checks them
    /// against the checked-in fixture. Run with <c>KAVITA_UPDATE_FIXTURES=1</c> to write the fixture instead
    /// </summary>
    [Fact]
    public async Task ParityFixture_MatchesThePageResponseAndTheLookupAnswers()
    {
        var book = await Setup();
        var unit = GivenParityPage(book.FirstFile, 2);

        var page = await book.Service.GetEpubPageAsync(UserId, book.First.Id, 2);
        var taps = new JsonArray();
        foreach (var (block, s, sub, phrase) in ParityTaps)
        {
            var result = await book.Service.LookupAsync(UserId, new DictionaryLookupRequestDto(book.First.Id, 2, block, s, sub, phrase));
            taps.Add(new JsonObject
            {
                ["request"] = new JsonObject { ["block"] = block, ["s"] = s, ["sub"] = sub, ["phrase"] = phrase },
                ["result"] = JsonSerializer.SerializeToNode(result, Web),
            });
        }

        var fixture = new JsonObject
        {
            ["page"] = JsonSerializer.SerializeToNode(page, Web),
            // The reader rebuilds these from the DOM
            ["texts"] = new JsonObject(unit.Blocks.Select(b =>
                new KeyValuePair<string, JsonNode?>(b.Index.ToString(), JsonValue.Create(b.Text)))),
            ["taps"] = taps,
        };
        var json = fixture.ToJsonString(new JsonSerializerOptions(Web) { WriteIndented = true }) + "\n";

        if (Environment.GetEnvironmentVariable("KAVITA_UPDATE_FIXTURES") == "1")
        {
            await File.WriteAllTextAsync(ParityFixturePath, json);
            return;
        }

        Assert.True(File.Exists(ParityFixturePath), $"Run with KAVITA_UPDATE_FIXTURES=1 to write {ParityFixturePath}");
        var stored = (await File.ReadAllTextAsync(ParityFixturePath)).ReplaceLineEndings("\n");
        Assert.True(stored == json, "The page response or a lookup answer changed. Run the test with " +
                                    "KAVITA_UPDATE_FIXTURES=1 to write the fixture, then run the UI's local-lookup spec");
    }

    #endregion

    #region Book words

    [Fact]
    public async Task GetEpubPage_JoinsTheSeriesBookWordsAndGivesEachItsBookWordHint()
    {
        var book = await Setup();
        GivenParityPage(book.FirstFile, 2);

        var page = await book.Service.GetEpubPageAsync(UserId, book.First.Id, 2);

        Assert.NotNull(page);
        await _store.Received().GetBookWordsAsync(
            Arg.Is<IReadOnlyCollection<int>>(f => f.Order().SequenceEqual(new[] { book.FirstFile.Id, book.SecondFile.Id }.Order())),
            Arg.Any<CancellationToken>());
        var block = page.Blocks.Single(b => b.Index == 11);
        Assert.Equal([(0, 2, (long?) Suirei.Id), (2, 3, null), (3, 5, Suirei.Id), (5, 6, null)], block.Tokens.Select(t => (t.S, t.E, t.W)));
        Assert.Null(block.Blind);
        Assert.Equal(new ReadingHintDto("すいれい", "スイレイ", ReadingSource.Book), page.Words[Suirei.Id].Hint);
        Assert.Equal(new ReadingHintDto("ばせん", "バセン", ReadingSource.Series), page.Words[Basen.Id].Hint);
        Assert.Empty(page.Words[Suirei.Id].Entries!);
        Assert.DoesNotContain(page.Analysis, a => a.Block == 11);
        // The pieces stay on the page as parts, and the hint doesn't come from reading_mark
        Assert.Contains(Midori.Id, page.Words.Keys);
        await _store.DidNotReceive().GetReadingMarksAsync(Arg.Any<int>(),
            Arg.Is<IReadOnlyCollection<long>>(ids => ids.Contains(Suirei.Id)), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Lookup_OfABookWord_ReadsTheFuriganaWhenMarkedAndTheHintWhenNot()
    {
        var book = await Setup();
        GivenParityPage(book.FirstFile, 2);

        var marked = await ReadingsAt(book, 11, 0);
        var unmarked = await ReadingsAt(book, 11, 3);
        var series = await ReadingsAt(book, 12, 0);

        Assert.Equal((ReadingLevel.Certain, ReadingSource.Furigana, "スイレイ", (string?) null, true, 0),
            (marked.Level, marked.Source, marked.Shown, marked.Pick, marked.BookOnly, marked.Groups.Count));
        Assert.Equal((ReadingLevel.Likely, ReadingSource.Book, "スイレイ", (string?) null, false, 0),
            (unmarked.Level, unmarked.Source, unmarked.Shown, unmarked.Pick, unmarked.BookOnly, unmarked.Groups.Count));
        Assert.Equal((ReadingLevel.Likely, ReadingSource.Series, "バセン", (string?) null, false, 0),
            (series.Level, series.Source, series.Shown, series.Pick, series.BookOnly, series.Groups.Count));
    }

    #endregion

    #region Readings

    private async Task<ReadingsDto> ReadingsAt(Book book, int block, int s, int? sub = null)
    {
        var result = await book.Service.LookupAsync(UserId, new DictionaryLookupRequestDto(book.First.Id, 2, block, s, sub));
        Assert.NotNull(result);
        return result.Readings;
    }

    [Fact]
    public async Task GetEpubPage_CarriesTheSeriesBlindOffsetsEntriesHintsAndTheUsersChoices()
    {
        var book = await Setup();
        GivenParityPage(book.FirstFile, 2);

        var page = await book.Service.GetEpubPageAsync(UserId, book.First.Id, 2);

        Assert.NotNull(page);
        Assert.Equal(book.Series.Id, page.SeriesId);
        Assert.Equal([2], page.Blocks.Single(b => b.Index == 5).Blind);
        Assert.Null(page.Blocks.Single(b => b.Index == 6).Blind);
        Assert.Equal(3, page.Words[Blink.Id].Entries!.Count);
        Assert.Equal(["こんにち"], page.Words[Kyou.Id].Senses[1].R!);
        Assert.Null(page.Words[Karai.Id].Entries);
        Assert.Empty(page.Words[Gaoshun.Id].Entries!);
        Assert.Equal(new ReadingHintDto("あく", "あく", ReadingSource.Book), page.Words[Hiraku.Id].Hint);
        Assert.Equal(new ReadingHintDto("せい", "せい", ReadingSource.Series), page.Words[Hoshi.Id].Hint);
        Assert.Equal(new ReadingHintDto("がおしゅん", "ガオシュン", ReadingSource.Book), page.Words[Gaoshun.Id].Hint);
        Assert.Null(page.Words[Blink.Id].Hint);
        Assert.Equal("うわて", page.Words[Jouzu.Id].Choice);
        Assert.Null(page.Words[Blink.Id].Choice);
        // Only words the file gives no hint go to the series
        await _store.Received(1).GetReadingMarksAsync(Arg.Any<IReadOnlyCollection<int>>(),
            Arg.Is<IReadOnlyCollection<long>>(ids => !ids.Contains(Hiraku.Id) && ids.Contains(Hoshi.Id)), true, Arg.Any<CancellationToken>());
        await _store.Received(1).GetReadingChoicesAsync(UserId, book.Series.Id, Arg.Any<IReadOnlyCollection<long>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetEpubPage_AsksTheSeriesForTheSeriesEpubFilesOnly()
    {
        var book = await Setup();
        GivenParityPage(book.FirstFile, 2);

        await book.Service.GetEpubPageAsync(UserId, book.First.Id, 2);

        await _store.Received(1).GetReadingMarksAsync(
            Arg.Is<IReadOnlyCollection<int>>(f => f.Order().SequenceEqual(new[] { book.FirstFile.Id, book.SecondFile.Id }.Order())),
            Arg.Any<IReadOnlyCollection<long>>(), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetEpubPage_WritesEntriesHintAndChoiceOnlyWhenTheWordHasThem()
    {
        var book = await Setup();
        GivenParityPage(book.FirstFile, 2);

        var page = await book.Service.GetEpubPageAsync(UserId, book.First.Id, 2);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(page, Web));

        var words = json.RootElement.GetProperty("words");
        Assert.False(words.GetProperty(Karai.Id.ToString()).TryGetProperty("entries", out _));
        Assert.False(words.GetProperty(Karai.Id.ToString()).TryGetProperty("hint", out _));
        Assert.False(words.GetProperty(Karai.Id.ToString()).TryGetProperty("choice", out _));
        Assert.Equal("[]", words.GetProperty(Gaoshun.Id.ToString()).GetProperty("entries").GetRawText());
        Assert.Equal("""{"reading":"あく","printed":"あく","source":"book"}""",
            words.GetProperty(Hiraku.Id.ToString()).GetProperty("hint").GetRawText());
        Assert.Equal(book.Series.Id, json.RootElement.GetProperty("seriesId").GetInt32());
    }

    [Fact]
    public async Task Lookup_ResolvesTheReadingsOfTheTappedWord()
    {
        var book = await Setup();
        GivenParityPage(book.FirstFile, 2);

        var blink = await ReadingsAt(book, 5, 2);
        Assert.Equal((ReadingLevel.Guess, ReadingSource.Dictionary, "またたく"), (blink.Level, blink.Source, blink.Pick));
        Assert.Equal([1341200L, 2831002L, 2831001L], blink.Groups.Select(g => g.Seq));

        var star = await ReadingsAt(book, 5, 0);
        Assert.Equal((ReadingLevel.Likely, ReadingSource.Series, "せい"), (star.Level, star.Source, star.Pick));

        var open = await ReadingsAt(book, 6, 2);
        Assert.Equal((ReadingSource.Book, "あく"), (open.Source, open.Pick));

        var skilful = await ReadingsAt(book, 7, 0);
        Assert.Equal((ReadingLevel.Likely, ReadingSource.Choice, "うわて"), (skilful.Level, skilful.Source, skilful.Pick));
        Assert.Equal(["うわて"], skilful.Groups[0].Readings);
        Assert.Equal(3, skilful.Groups.Count);

        var say = await ReadingsAt(book, 7, 3);
        Assert.Equal(new ReadingsDto(ReadingLevel.Certain, ReadingSource.Dictionary, null, "いう", false, say.Groups), say);

        var marked = await ReadingsAt(book, 8, 3);
        Assert.Equal(new ReadingsDto(ReadingLevel.Certain, ReadingSource.Furigana, "こんにち", "こんにち", false, marked.Groups), marked);
        Assert.Equal(2, Assert.Single(marked.Groups).Senses.Count);

        var unmarked = await ReadingsAt(book, 8, 0);
        Assert.Equal((ReadingSource.Tokenizer, "きょう", 2), (unmarked.Source, unmarked.Pick, unmarked.Groups.Count));

        var lady = await ReadingsAt(book, 9, 0);
        Assert.Equal(new ReadingsDto(ReadingLevel.Certain, ReadingSource.Furigana, "ねえちゃん", null, true, lady.Groups), lady);
        Assert.Empty(lady.Groups);

        var name = await ReadingsAt(book, 9, 3);
        Assert.Equal(new ReadingsDto(ReadingLevel.Likely, ReadingSource.Book, "ガオシュン", null, false, name.Groups), name);
    }

    [Fact]
    public async Task Lookup_OfAWordStoredWithoutEntriesIgnoresItsFuriganaAndHint()
    {
        var book = await Setup();
        GivenParityPage(book.FirstFile, 2);

        var tanaka = await ReadingsAt(book, 3, 3);

        Assert.Equal(ReadingsDto.None with { Groups = tanaka.Groups }, tanaka);
        Assert.Empty(tanaka.Groups);
    }

    [Fact]
    public async Task Lookup_OfAPartUsesItsOwnReadingAndNoChoiceOrHint()
    {
        var book = await Setup();
        GivenParityPage(book.FirstFile, 2);
        GivenChoices(new Dictionary<long, string> { [Blink.Id] = "めたたく" });

        var part = await ReadingsAt(book, 10, 0, 0);
        var star = await ReadingsAt(book, 10, 0, 1);

        Assert.Equal((ReadingLevel.Likely, ReadingSource.Tokenizer, "またたく"), (part.Level, part.Source, part.Pick));
        // ぼし is in no group, and the series hint for 星 doesn't count for a part
        Assert.Equal((ReadingLevel.Guess, ReadingSource.Dictionary, "ほし"), (star.Level, star.Source, star.Pick));
    }

    [Fact]
    public async Task SaveReadingChoice_StoresAnyReadingOfAnyGroup()
    {
        var book = await Setup();
        GivenWords(Blink);

        await book.Service.SaveReadingChoiceAsync(UserId, new ReadingChoiceDto(book.Series.Id, Blink.Id, "まばたく"));

        await _store.Received(1).SaveReadingChoiceAsync(UserId, book.Series.Id, Blink.Id, "まばたく", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(90, "まじまじ")] // not a reading of 瞬く
    [InlineData(90, "")]
    [InlineData(777, "またたく")] // no such word
    [InlineData(11, "からい")] // 辛い is stored without entries, so it has no groups
    public async Task SaveReadingChoice_RefusesAReadingNoGroupHas(long wordId, string reading)
    {
        var book = await Setup();
        GivenWords(Blink, Karai);

        await Assert.ThrowsAsync<KavitaException>(() =>
            book.Service.SaveReadingChoiceAsync(UserId, new ReadingChoiceDto(book.Series.Id, wordId, reading)));
        await _store.DidNotReceiveWithAnyArgs().SaveReadingChoiceAsync(default, default, default, default!, default);
    }

    [Fact]
    public async Task ClearReadingChoice_ClearsTheUsersRow()
    {
        var book = await Setup();

        await book.Service.ClearReadingChoiceAsync(UserId, book.Series.Id, Blink.Id);

        await _store.Received(1).ClearReadingChoiceAsync(UserId, book.Series.Id, Blink.Id, Arg.Any<CancellationToken>());
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
    public async Task AnalyzeSeries_RequestsEveryJapaneseEpubOfTheSeriesAtWholeBookPriority()
    {
        var book = await Setup();

        var count = await book.Service.AnalyzeSeriesAsync(book.Series.Id);

        Assert.Equal(2, count);
        await _store.Received(1).RequestAnalysisAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.Order().SequenceEqual(new[] { book.FirstFile.Id, book.SecondFile.Id }.Order())),
            ReadingPriority.WholeBook, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnalyzeSeries_RequestsNothingWhenTheLibraryHasTheDictionaryOff()
    {
        var book = await Setup(enableDictionary: false);

        Assert.Equal(0, await book.Service.AnalyzeSeriesAsync(book.Series.Id));
        await _store.DidNotReceiveWithAnyArgs().RequestAnalysisAsync(default!, default, default);
    }

    [Theory]
    [InlineData("ja", "", true)]
    [InlineData("JA-jp", "", true)]
    [InlineData("ja_JP", "", true)]
    [InlineData("jpn", "", true)]
    [InlineData("", "ja", true)]
    [InlineData("en", "ja", false)]
    [InlineData("", "", false)]
    [InlineData("jav", "", false)]
    public async Task AnalyzeSeries_TakesTheChapterLanguageAndFallsBackToTheSeries(string chapterLanguage,
        string seriesLanguage, bool requested)
    {
        var book = await Setup(firstLanguage: chapterLanguage, secondLanguage: "en", seriesLanguage: seriesLanguage);

        Assert.Equal(requested ? 1 : 0, await book.Service.AnalyzeSeriesAsync(book.Series.Id));
    }

    [Fact]
    public async Task RemoveSeries_ClearsTheAnalysisOfEveryEpubOfTheSeriesEvenWhenNotEligible()
    {
        var book = await Setup(enableDictionary: false, firstLanguage: "en");

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
        GivenQueue(GivenUnit(5, book.FirstFile.Id, 0, blocks, rows));

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
    public async Task GetPendingAnalysis_ReadsMoreOfTheQueueWhenTheReadStopsInsideABlock()
    {
        var book = await Setup();
        GivenWords(Karai);
        // 70 rows in one block, so the first read of 64 holds only part of it
        var text = string.Concat(Enumerable.Repeat("辛い", 70));
        var rows = Enumerable.Range(0, 70).Select(i => Row(i + 1, 5, 0, i * 2, i * 2 + 2, Karai.Id)).ToArray();
        GivenQueue(GivenUnit(5, book.FirstFile.Id, 0, [text], rows));

        var passage = Assert.Single(await book.Service.GetPendingAnalysisAsync(1, 0));

        Assert.Equal(70, passage.Items.Count);
        await _store.Received(1).GetPendingAnalysisAsync(64, 0, Arg.Any<CancellationToken>());
        await _store.Received(1).GetPendingAnalysisAsync(256, 0, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetPendingAnalysis_TakesRowTextFromTheQueueAndOnlyContextFromTheUnit()
    {
        var book = await Setup();
        GivenWords(Karai);
        // The unit was tokenized again after the queue read, so its stored text for block 1 is newer than the rows
        GivenUnit(5, book.FirstFile.Id, 0, ["前の文。", "新しい文"]);
        GivenQueue([new PendingAnalysisRow(1, 5, book.FirstFile.Id, 0, 1, 0, 2, Karai.Id, 30, "辛い")]);

        var passage = Assert.Single(await book.Service.GetPendingAnalysisAsync(4, 0));

        Assert.Equal("前の文。\n辛い", passage.Text);
        Assert.Equal("辛い", passage.Items.Single().Surface);
        AssertItemsMatchText(passage);
    }

    [Fact]
    public async Task GetPendingAnalysis_BuildsFromTheQueueTextWhenTheUnitIsGone()
    {
        var book = await Setup();
        GivenWords(Karai);
        GivenQueue([new PendingAnalysisRow(1, 900, book.FirstFile.Id, 9, 3, 0, 2, Karai.Id, 30, "辛い")]);

        var passage = Assert.Single(await book.Service.GetPendingAnalysisAsync(4, 0));

        Assert.Equal("辛い", passage.Text);
        Assert.Equal(1, passage.Items.Single().Id);
    }

    [Fact]
    public async Task GetPendingAnalysis_SettlesRowsThatDontFitTheirTextOrHaveNoWordWithoutSpendingTheLimit()
    {
        var book = await Setup();
        GivenWords(Karai);
        GivenQueue(
            GivenUnit(5, book.FirstFile.Id, 0, ["辛い"], Row(1, 5, 0, 0, 9, Karai.Id, priority: 30))
                .Concat(GivenUnit(6, book.FirstFile.Id, 1, ["辛い"], Row(2, 6, 0, 0, 2, 999, priority: 30)))
                .Concat(GivenUnit(7, book.FirstFile.Id, 2, ["辛い"], Row(3, 7, 0, 0, 2, Karai.Id, priority: 20))));

        var passage = Assert.Single(await book.Service.GetPendingAnalysisAsync(1, 0));

        Assert.Equal(3, passage.Items.Single().Id);
        await _store.Received(1).StoreAnalysisAsync(DictionaryService.UnusableRowGenerator, "none",
            Arg.Is<IReadOnlyList<AnalysisAnswer>>(a => a.SequenceEqual(new[] { new AnalysisAnswer(1, null, null), new AnalysisAnswer(2, null, null) })),
            Arg.Any<CancellationToken>());
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
        // No passage was served in this process, as after a restart, so the event comes from StoredPages alone
        var book = await Setup();
        _store.StoreAnalysisAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<AnalysisAnswer>>(), Arg.Any<CancellationToken>())
            .Returns(new AnalysisStoreOutcome(3, [new AnalysisRejection(4, AnalysisRejectReason.SenseNotOffered)])
            {
                StoredPages = [new(book.FirstFile.Id, 0), new(book.FirstFile.Id, 4), new(book.SecondFile.Id, 1)],
            });

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

    [Fact]
    public async Task StoreAnalysis_ReturnsTheStoresCountForABatchOfNullAnswers()
    {
        var book = await Setup();
        _store.StoreAnalysisAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<AnalysisAnswer>>(), Arg.Any<CancellationToken>())
            .Returns(new AnalysisStoreOutcome(3, []) { StoredPages = [new(book.FirstFile.Id, 2)] });

        var result = await book.Service.StoreAnalysisAsync(Answers(
            new AnalysisAnswerDto(1, null, null),
            new AnalysisAnswerDto(2, null, null),
            new AnalysisAnswerDto(3, null, null)));

        Assert.Equal(3, result.Stored);
        Assert.Empty(result.Rejected);
        await _store.Received(1).StoreAnalysisAsync("claude-sonnet", "p1",
            Arg.Is<IReadOnlyList<AnalysisAnswer>>(a => a.Count == 3 && a.All(x => x.SenseKey == null && x.Reading == null)),
            Arg.Any<CancellationToken>());
        await _eventHub.Received(1).SendMessageAsync(MessageFactory.DictionaryAnalysisReady,
            Arg.Is<SignalRMessageDto>(m => IsReady(m, book.Series.Id, book.First.Id, 2)), false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StoreAnalysis_PairsEachRejectionOfARepeatedIdWithItsOwnAnswer()
    {
        var book = await Setup();
        _store.StoreAnalysisAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<AnalysisAnswer>>(), Arg.Any<CancellationToken>())
            .Returns(new AnalysisStoreOutcome(0, [
                new AnalysisRejection(1, AnalysisRejectReason.SenseNotOffered),
                new AnalysisRejection(1, AnalysisRejectReason.SenseNotOffered),
            ]));

        var result = await book.Service.StoreAnalysisAsync(Answers(new AnalysisAnswerDto(1, "a", null), new AnalysisAnswerDto(1, "b", null)));

        Assert.Equal(["sense a was not offered", "sense b was not offered"], result.Rejected.Select(r => r.Reason));
    }

    [Fact]
    public void StoreAnalysis_PairsADuplicateWithTheAnswerAfterTheStoredOne()
    {
        var answers = new[] { new AnalysisAnswer(1, "a", null), new AnalysisAnswer(2, null, "x"), new AnalysisAnswer(1, "b", null) };
        var rejections = new[]
        {
            new AnalysisRejection(2, AnalysisRejectReason.ReadingNotOffered),
            new AnalysisRejection(1, AnalysisRejectReason.Duplicate),
        };

        var pairs = DictionaryService.PairRejections(answers, rejections).ToList();

        Assert.Equal([answers[1], answers[2]], pairs.Select(p => p.Answer));
    }

    [Fact]
    public async Task StoreAnalysis_StillAnswersWhenTellingReadersFails()
    {
        var book = await Setup();
        _store.StoreAnalysisAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<AnalysisAnswer>>(), Arg.Any<CancellationToken>())
            .Returns(new AnalysisStoreOutcome(2, [new AnalysisRejection(3, AnalysisRejectReason.NotPending)])
            {
                StoredPages = [new(book.FirstFile.Id, 0)],
            });
        _eventHub.SendMessageAsync(Arg.Any<string>(), Arg.Any<SignalRMessageDto>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("hub is down")));

        var result = await book.Service.StoreAnalysisAsync(Answers(
            new AnalysisAnswerDto(1, null, null), new AnalysisAnswerDto(2, null, null), new AnalysisAnswerDto(3, null, null)));

        Assert.Equal(2, result.Stored);
        Assert.Equal([3L], result.Rejected.Select(r => r.Id));
        await _eventHub.Received(1).SendMessageAsync(MessageFactory.DictionaryAnalysisReady, Arg.Any<SignalRMessageDto>(),
            false, CancellationToken.None);
    }

    [Fact]
    public async Task StoreAnalysis_RefusesAMissingItemsListOrANullItem()
    {
        var book = await Setup();

        await Assert.ThrowsAsync<KavitaException>(() => book.Service.StoreAnalysisAsync(Answers() with { Items = null! }));
        await Assert.ThrowsAsync<KavitaException>(() => book.Service.StoreAnalysisAsync(Answers(new AnalysisAnswerDto[] { null! })));
        await _store.DidNotReceiveWithAnyArgs().StoreAnalysisAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task StoreAnalysis_SendsNothingWhenNoPageGotAnswers()
    {
        var book = await Setup();
        _store.StoreAnalysisAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<AnalysisAnswer>>(), Arg.Any<CancellationToken>())
            .Returns(new AnalysisStoreOutcome(0, [new AnalysisRejection(1, AnalysisRejectReason.NotPending)]));

        var result = await book.Service.StoreAnalysisAsync(Answers(new AnalysisAnswerDto(1, null, null)));

        Assert.Equal(0, result.Stored);
        await _eventHub.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default, default);
    }

    private static bool IsReady(SignalRMessageDto message, int seriesId, int chapterId, params int[] pages)
    {
        return message.Body is DictionaryAnalysisReadyEventBodyDto body && body.SeriesId == seriesId &&
               body.ChapterId == chapterId && body.Pages.SequenceEqual(pages);
    }

    #endregion
}
