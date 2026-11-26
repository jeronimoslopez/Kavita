using System.Globalization;
using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using Flurl.Http.Testing;
using HtmlAgilityPack;
using Kavita.API.Database;
using Kavita.API.Services;
using Kavita.API.Services.Plus;
using Kavita.API.Services.Reading;
using Kavita.API.Services.SignalR;
using Kavita.Database;
using Kavita.Database.Tests;
using Kavita.Models.Builders;
using Kavita.Models.DTOs.Dictionary;
using Kavita.Models.Entities;
using Kavita.Models.Entities.Enums;
using Kavita.Services.Builders;
using Kavita.Services.Dictionary;
using Kavita.Services.Reading;
using Kavita.Services.Scanner;
using Kavita.Services.Tests.Helpers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit.Abstractions;

namespace Kavita.Services.Tests.Dictionary;

[Collection(HangfireStorageCollection.Name)]
public class TokenizeServiceTests(ITestOutputHelper outputHelper) : AbstractDbTest(outputHelper), IDisposable
{
    private const string SidecarUrl = "http://text:8000";
    private const int PageCount = 4;
    private static readonly DateTime WriteTime = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string Stamp = WriteTime.Ticks.ToString(CultureInfo.InvariantCulture);
    private static readonly string NewerStamp = WriteTime.AddHours(1).Ticks.ToString(CultureInfo.InvariantCulture);

    private static readonly string SampleRequest = File.ReadAllText(
        Path.Join(Directory.GetCurrentDirectory(), "../../../../text-sidecar-contract/sample-request.json"));
    private static readonly string SampleResponse = File.ReadAllText(
        Path.Join(Directory.GetCurrentDirectory(), "../../../../text-sidecar-contract/sample-response.json"));

    private readonly IReadingStore _store = Substitute.For<IReadingStore>();
    private readonly IEpubPageSource _pages = Substitute.For<IEpubPageSource>();
    private readonly ITokenizeWorkerSignal _signal = Substitute.For<ITokenizeWorkerSignal>();
    private readonly MockFileSystem _fileSystem = new();
    private readonly HttpTest _httpTest = new();

    public void Dispose() => _httpTest.Dispose();

    /// <summary>
    /// The files of the library <see cref="Setup"/> builds
    /// </summary>
    private sealed record Files(MangaFile Chapter1, MangaFile Chapter2, MangaFile SeriesLanguage, MangaFile English,
        MangaFile Pdf, MangaFile Disabled, Chapter FirstChapter, Chapter SecondChapter);

    /// <summary>
    /// A dictionary library with a Japanese series of two EPUB chapters and a Japanese PDF, a series whose chapter has
    /// no language and whose metadata says Japanese, and an English series. Then a library without the dictionary
    /// holding a Japanese EPUB
    /// </summary>
    private async Task<(DataContext, TokenizeService, Files)> Setup(bool sidecarUrl = true)
    {
        var (unitOfWork, context, _) = await CreateDatabase();
        TokenizeService.ForgetUnusableFiles();

        var chapter1 = Epub("/books/Kokoro/Kokoro 1.epub");
        var chapter2 = Epub("/books/Kokoro/Kokoro 2.epub");
        var pdf = new MangaFileBuilder("/books/Kokoro/Kokoro notes.pdf", MangaFormat.Pdf, 3).Build();
        AddToDisk(pdf.FilePath);
        var first = new ChapterBuilder("1").WithFile(chapter1).Build();
        first.Language = "ja";
        var second = new ChapterBuilder("2").WithFile(chapter2).WithFile(pdf).Build();
        second.Language = "ja-JP";
        var kokoro = new SeriesBuilder("Kokoro")
            .WithFormat(MangaFormat.Epub)
            .WithVolume(new VolumeBuilder(Parser.LooseLeafVolume).WithChapter(first).WithChapter(second).Build())
            .Build();

        var seriesLanguage = Epub("/books/Sanshiro/Sanshiro.epub");
        var sanshiro = SingleChapterSeries("Sanshiro", seriesLanguage, null);
        sanshiro.Metadata.Language = "ja";

        var english = Epub("/books/Dracula/Dracula.epub");
        var dracula = SingleChapterSeries("Dracula", english, "en");
        dracula.Metadata.Language = "ja";

        var library = new LibraryBuilder("Books", LibraryType.Book).WithSeries(kokoro).WithSeries(sanshiro).WithSeries(dracula).Build();
        library.EnableDictionary = true;

        var disabled = Epub("/other/Botchan/Botchan.epub");
        var otherLibrary = new LibraryBuilder("Other", LibraryType.Book)
            .WithSeries(SingleChapterSeries("Botchan", disabled, "ja"))
            .Build();

        context.Library.Add(library);
        context.Library.Add(otherLibrary);
        if (sidecarUrl) context.ServerSetting.Single(s => s.Key == ServerSettingKey.TextSidecarUrl).Value = SidecarUrl;
        await context.SaveChangesAsync();

        _pages.GetEpubPageCountAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(PageCount);
        _store.GetFileStampsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<int, string>());
        _store.SaveTokenizedUnitAsync(default, default!, default!, default!, default).ReturnsForAnyArgs(true);

        var service = new TokenizeService(Substitute.For<ILogger<TokenizeService>>(), unitOfWork, _pages,
            new DirectoryService(Substitute.For<ILogger<DirectoryService>>(), _fileSystem), _store, new TextSidecarClient(),
            _signal, CreateReaderService(unitOfWork));
        return (context, service, new Files(chapter1, chapter2, seriesLanguage, english, pdf, disabled, first, second));
    }

    private MangaFile Epub(string path)
    {
        AddToDisk(path);
        return new MangaFileBuilder(path, MangaFormat.Epub, PageCount).Build();
    }

    private void AddToDisk(string path) => _fileSystem.AddFile(path, new MockFileData("epub") { LastWriteTime = WriteTime });

    private static Series SingleChapterSeries(string name, MangaFile file, string? language)
    {
        var chapter = new ChapterBuilder("1").WithFile(file).Build();
        chapter.Language = language;
        return new SeriesBuilder(name)
            .WithFormat(MangaFormat.Epub)
            .WithVolume(new VolumeBuilder(Parser.LooseLeafVolume).WithChapter(chapter).Build())
            .Build();
    }

    private static ReaderService CreateReaderService(IUnitOfWork unitOfWork)
    {
        return new ReaderService(unitOfWork, Substitute.For<ILogger<ReaderService>>(),
            Substitute.For<IEventHub>(), Substitute.For<IImageService>(),
            new DirectoryService(Substitute.For<ILogger<DirectoryService>>(), new MockFileSystem()),
            Substitute.For<IScrobblingService>(), Substitute.For<IReadingSessionService>(),
            Substitute.For<IClientInfoAccessor>(), Substitute.For<IEntityNamingService>(),
            Substitute.For<ILocalizationService>(), Substitute.For<IBookService>());
    }

    private static ReadingUnitRef Unit(MangaFile file, int page = 0, string? stamp = null) =>
        new(7, ReadingUnitKind.Epub, file.Id, page, stamp ?? Stamp, 0);

    private static List<EpubTextBlock> SampleBlocks()
    {
        using var doc = JsonDocument.Parse(SampleRequest);
        return doc.RootElement.GetProperty("blocks").EnumerateArray()
            .Select((b, i) => Block(i, b.GetProperty("text").GetString()!))
            .ToList();
    }

    private static EpubTextBlock Block(int index, string text) => new(index, HtmlNode.CreateNode("<p></p>"), text);

    private void ReturnWordIds()
    {
        _store.UpsertWordsAsync(default!, default!, default).ReturnsForAnyArgs(call =>
            (IReadOnlyList<long>) call.Arg<IReadOnlyList<SidecarWordDto>>().Select((_, i) => 100L + i).ToList());
    }

    private (IReadOnlyList<StoredBlock> Blocks, string Version) SavedUnit()
    {
        var call = _store.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IReadingStore.SaveTokenizedUnitAsync));
        var args = call.GetArguments();
        return ((IReadOnlyList<StoredBlock>) args[3]!, (string) args[2]!);
    }

    #region Detection

    [Fact]
    public async Task DetectNewFiles_SeedsOnlyJapaneseEpubsInDictionaryLibraries()
    {
        var (_, service, files) = await Setup();

        await service.DetectNewFilesAsync();

        var seeded = _store.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IReadingStore.SeedFileAsync))
            .Select(c => (int) c.GetArguments()[0]!)
            .Order()
            .ToList();
        Assert.Equal(new[] { files.Chapter1.Id, files.Chapter2.Id, files.SeriesLanguage.Id }.Order(), seeded);
        await _store.Received().SeedFileAsync(files.Chapter1.Id, PageCount, Stamp, ReadingPriority.WholeBook, Arg.Any<CancellationToken>());
        await _store.DidNotReceiveWithAnyArgs().DeleteFilesAsync(default!);
        _signal.Received().Wake();
    }

    [Fact]
    public async Task DetectNewFiles_UnchangedFiles_DoNothing()
    {
        var (_, service, files) = await Setup();
        _store.GetFileStampsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<int, string>
        {
            [files.Chapter1.Id] = Stamp, [files.Chapter2.Id] = Stamp, [files.SeriesLanguage.Id] = Stamp,
        });

        await service.DetectNewFilesAsync();

        await _pages.DidNotReceiveWithAnyArgs().GetEpubPageCountAsync(default!);
        await _store.DidNotReceiveWithAnyArgs().SeedFileAsync(default, default, default!, default);
        await _store.DidNotReceiveWithAnyArgs().DeleteFilesAsync(default!);
        _signal.DidNotReceive().Wake();
    }

    [Fact]
    public async Task DetectNewFiles_ChangedStamp_SeedsTheFileAgain()
    {
        var (_, service, files) = await Setup();
        _store.GetFileStampsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<int, string>
        {
            [files.Chapter1.Id] = WriteTime.AddDays(-1).Ticks.ToString(CultureInfo.InvariantCulture),
            [files.Chapter2.Id] = Stamp, [files.SeriesLanguage.Id] = Stamp,
        });

        await service.DetectNewFilesAsync();

        await _store.Received(1).SeedFileAsync(files.Chapter1.Id, PageCount, Stamp, ReadingPriority.WholeBook, Arg.Any<CancellationToken>());
        await _store.DidNotReceive().SeedFileAsync(files.Chapter2.Id, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DetectNewFiles_StampIsCutToTheSecond()
    {
        var (_, service, files) = await Setup();
        _fileSystem.GetFile(files.Chapter1.FilePath).LastWriteTime = WriteTime.AddMilliseconds(700);
        _store.GetFileStampsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<int, string>
        {
            [files.Chapter1.Id] = Stamp, [files.Chapter2.Id] = Stamp, [files.SeriesLanguage.Id] = Stamp,
        });

        await service.DetectNewFilesAsync();

        await _store.DidNotReceiveWithAnyArgs().SeedFileAsync(default, default, default!, default);
    }

    [Fact]
    public async Task DetectNewFiles_DeletesFilesThatAreGoneOrNoLongerQualify()
    {
        var (_, service, files) = await Setup();
        const int deletedFileId = 999;
        _store.GetFileStampsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<int, string>
        {
            [files.Chapter1.Id] = Stamp, [files.Chapter2.Id] = Stamp, [files.SeriesLanguage.Id] = Stamp,
            [files.English.Id] = Stamp, [files.Disabled.Id] = Stamp, [deletedFileId] = Stamp,
        });

        await service.DetectNewFilesAsync();

        await _store.Received(1).DeleteFilesAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.Order().SequenceEqual(new[] { files.English.Id, files.Disabled.Id, deletedFileId }.Order())),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DetectNewFiles_FileMissingFromDisk_KeepsItsUnits()
    {
        var (_, service, files) = await Setup();
        _fileSystem.RemoveFile(files.Chapter1.FilePath);

        await service.DetectNewFilesAsync();

        await _store.DidNotReceive().SeedFileAsync(files.Chapter1.Id, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceiveWithAnyArgs().DeleteFilesAsync(default!);
    }

    private static bool DetectionQueued() =>
        Kavita.Services.TaskScheduler.HasAlreadyEnqueuedTask(nameof(ITokenizeService), nameof(ITokenizeService.DetectNewFilesAsync), []);

    [Fact]
    public async Task MarkSeriesForCheck_NewFiles_QueuesDetectionWithoutOpeningThem()
    {
        var (context, service, _) = await Setup();
        var kokoroId = context.Series.Single(s => s.Name == "Kokoro").Id;

        await service.MarkSeriesForCheckAsync(kokoroId);

        Assert.True(DetectionQueued());
        await _pages.DidNotReceiveWithAnyArgs().GetEpubPageCountAsync(default!);
        await _store.DidNotReceiveWithAnyArgs().SeedFileAsync(default, default, default!, default);
        await _store.DidNotReceiveWithAnyArgs().DeleteFilesAsync(default!);
    }

    [Fact]
    public async Task MarkSeriesForCheck_UnchangedFiles_QueuesNothing()
    {
        var (context, service, files) = await Setup();
        _store.GetFileStampsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<int, string>
        {
            [files.Chapter1.Id] = Stamp, [files.Chapter2.Id] = Stamp,
        });

        await service.MarkSeriesForCheckAsync(context.Series.Single(s => s.Name == "Kokoro").Id);

        Assert.False(DetectionQueued());
    }

    [Theory]
    [InlineData("Botchan", true)] // its library has the dictionary off
    [InlineData("Dracula", true)] // it's English
    [InlineData("Kokoro", false)] // no sidecar URL is set
    public async Task MarkSeriesForCheck_SeriesWithNothingToTokenize_LeavesTheStoreAlone(string series, bool sidecarUrl)
    {
        var (context, service, _) = await Setup(sidecarUrl);

        await service.MarkSeriesForCheckAsync(context.Series.Single(s => s.Name == series).Id);

        Assert.Empty(_store.ReceivedCalls());
        Assert.False(DetectionQueued());
    }

    [Fact]
    public async Task DetectNewFiles_UnreadableEpub_IsDeletedAndOpenedAgainOnlyOnceItChanges()
    {
        var (_, service, files) = await Setup();
        _pages.GetEpubPageCountAsync(files.Chapter1.FilePath, Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidDataException("not a zip"), _ => PageCount);

        await service.DetectNewFilesAsync();
        await service.DetectNewFilesAsync();

        await _pages.Received(1).GetEpubPageCountAsync(files.Chapter1.FilePath, Arg.Any<CancellationToken>());
        await _store.DidNotReceive().SeedFileAsync(files.Chapter1.Id, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _store.Received(1).DeleteFilesAsync(Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { files.Chapter1.Id })),
            Arg.Any<CancellationToken>());

        _fileSystem.GetFile(files.Chapter1.FilePath).LastWriteTime = WriteTime.AddHours(1);
        await service.DetectNewFilesAsync();

        await _pages.Received(2).GetEpubPageCountAsync(files.Chapter1.FilePath, Arg.Any<CancellationToken>());
        await _store.Received(1).SeedFileAsync(files.Chapter1.Id, PageCount, NewerStamp, ReadingPriority.WholeBook, Arg.Any<CancellationToken>());
    }

    [Theory]
    [MemberData(nameof(TransientReadErrors))]
    public async Task DetectNewFiles_FileThatCantBeReadThisTime_KeepsItsRowsAndIsTriedAgain(Exception error)
    {
        var (_, service, files) = await Setup();
        _store.GetFileStampsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<int, string>
        {
            [files.Chapter1.Id] = WriteTime.AddDays(-1).Ticks.ToString(CultureInfo.InvariantCulture),
        });
        _pages.GetEpubPageCountAsync(files.Chapter1.FilePath, Arg.Any<CancellationToken>()).ThrowsAsync(error);

        await service.DetectNewFilesAsync();
        await service.DetectNewFilesAsync();

        await _pages.Received(2).GetEpubPageCountAsync(files.Chapter1.FilePath, Arg.Any<CancellationToken>());
        await _store.DidNotReceive().SeedFileAsync(files.Chapter1.Id, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceiveWithAnyArgs().DeleteFilesAsync(default!);
    }

    [Fact]
    public async Task DetectNewFiles_CancelledRead_KeepsTheFileRows()
    {
        var (_, service, files) = await Setup();
        _pages.GetEpubPageCountAsync(files.Chapter1.FilePath, Arg.Any<CancellationToken>()).ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(service.DetectNewFilesAsync);

        await _store.DidNotReceiveWithAnyArgs().DeleteFilesAsync(default!);
        await _store.DidNotReceive().SeedFileAsync(files.Chapter1.Id, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    public static TheoryData<Exception> TransientReadErrors() =>
        [new IOException("share dropped"), new UnauthorizedAccessException("locked")];

    #endregion

    #region Processing

    [Fact]
    public async Task ProcessUnit_MapsWordIndicesToStoreIds()
    {
        var (_, service, files) = await Setup();
        var blocks = SampleBlocks();
        blocks.Add(Block(3, string.Empty));
        _pages.GetEpubPageBlocksAsync(files.Chapter1.FilePath, 2, Arg.Any<CancellationToken>()).Returns(blocks);
        _httpTest.RespondWith(SampleResponse);
        ReturnWordIds();

        var result = await service.ProcessUnitAsync(Unit(files.Chapter1, page: 2), SidecarUrl);

        Assert.Equal(new TokenizeUnitResult(TokenizeUnitOutcome.UnitDone, files.FirstChapter.Volume.SeriesId,
            files.FirstChapter.Id, 2), result);
        var sample = JsonSerializer.Deserialize<TokenizeResponseDto>(SampleResponse)!;
        await _store.Received(1).UpsertWordsAsync(Arg.Is<IReadOnlyList<SidecarWordDto>>(w => w.Count == sample.Words.Count),
            TokenizeService.LexiconVersion(sample.Version), Arg.Any<CancellationToken>());

        var (stored, version) = SavedUnit();
        Assert.Equal(sample.Version, version);
        Assert.Equal([0, 1, 2, 3], stored.Select(b => b.Index));
        Assert.Equal(blocks.Select(b => b.Text), stored.Select(b => b.Text));
        Assert.Empty(stored[3].Tokens);

        // 経済政策 is word 4 with the parts 経済 (word 2) and 政策 (word 3)
        var compound = stored[0].Tokens[4];
        Assert.Equal(104, compound.W);
        Assert.Equal([new SubTokenDto(6, 8, 102, "けいざい"), new SubTokenDto(8, 10, 103, "せいさく")], compound.Sub);
        Assert.Null(stored[0].Tokens[1].W);
        Assert.Equal(108, stored[1].Tokens[4].W);
    }

    [Fact]
    public async Task ProcessUnit_PageOverTheSidecarLimits_IsSplitAndMerged()
    {
        var (_, service, files) = await Setup();
        // 600,000 code points in all go over the 500,000 a request may hold, so the first 16 blocks go in one request and the other 4 in a second
        var big = new string('あ', 30_000);
        var blocks = Enumerable.Range(0, 20).Select(i => Block(i, big)).ToList();
        blocks.Add(Block(20, new string('い', TextSidecarClient.MaxCodePointsPerBlock + 1)));
        _pages.GetEpubPageBlocksAsync(files.Chapter1.FilePath, 0, Arg.Any<CancellationToken>()).Returns(blocks);
        _httpTest
            .RespondWithJson(OneWordAnswer(Enumerable.Range(0, 16), "あ"))
            .RespondWithJson(OneWordAnswer(Enumerable.Range(16, 4), "あい", nullWordBlock: 17));
        ReturnWordIds();

        var result = await service.ProcessUnitAsync(Unit(files.Chapter1), SidecarUrl);

        Assert.Equal(TokenizeUnitOutcome.UnitDone, result.Outcome);
        Assert.Equal(2, _httpTest.CallLog.Count);
        Assert.Equal(Enumerable.Range(0, 16).Select(i => i.ToString()), SentIds(_httpTest.CallLog[0]));
        Assert.Equal(Enumerable.Range(16, 4).Select(i => i.ToString()), SentIds(_httpTest.CallLog[1]));

        var (stored, _) = SavedUnit();
        Assert.Equal(21, stored.Count);
        Assert.Equal(100, stored[0].Tokens[0].W);
        Assert.Equal(100, stored[15].Tokens[0].W);
        // The second answer's word 0 is the third word overall
        Assert.Equal(102, stored[16].Tokens[0].W);
        Assert.Equal(102, stored[16].Tokens[0].Sub![0].W);
        Assert.Null(stored[17].Tokens[0].W);
        Assert.Null(stored[17].Tokens[0].Sub![0].W);
        Assert.Equal(102, stored[18].Tokens[0].W);
        Assert.Empty(stored[20].Tokens);
        // Phrase word indices count from the start of their own answer too
        Assert.Equal([new PhraseDto(0, 3, 101)], stored[0].Phrases);
        Assert.Equal([new PhraseDto(0, 3, 103)], stored[16].Phrases);
        Assert.Empty(stored[20].Phrases);
    }

    private static IEnumerable<string> SentIds(Flurl.Http.FlurlCall call)
    {
        using var body = JsonDocument.Parse(call.RequestBody!);
        return body.RootElement.GetProperty("blocks").EnumerateArray().Select(b => b.GetProperty("id").GetString()!).ToList();
    }

    /// <summary>
    /// An answer with two words where every block's first token points at word 0, and its part at word 0 too, except in
    /// <paramref name="nullWordBlock"/>, whose token and part point at no word. A second token follows with no word, and
    /// a phrase over both points at word 1
    /// </summary>
    private static object OneWordAnswer(IEnumerable<int> blockIds, string lemma, int? nullWordBlock = null)
    {
        return new
        {
            version = "sudachipy=1;lexicon=test;sidecar=1",
            words = new[]
            {
                new { lemma, pos = "名詞", isProper = false, reading = "あ", jlpt = (int?) null, readings = new[] { "あ" }, senses = Array.Empty<object>() },
                new { lemma = "x", pos = "名詞", isProper = false, reading = "x", jlpt = (int?) null, readings = new[] { "x" }, senses = Array.Empty<object>() },
            },
            blocks = blockIds.Select(id =>
            {
                int? w = id == nullWordBlock ? null : 0;
                return new
                {
                    id = id.ToString(CultureInfo.InvariantCulture),
                    tokens = new object[]
                    {
                        new object?[] { 0, 2, w, null, new object[] { new object?[] { 0, 1, w, null } } },
                        new object?[] { 2, 3, null, null, null },
                    },
                    phrases = new[] { new[] { 0, 3, 1 } },
                };
            }),
        };
    }

    /// <summary>
    /// 気がした as 気, が, し and た, with the words 気, する and 気がする. Block 0 has a phrase over its first three tokens,
    /// and block 1 comes from a version 1 sidecar with no phrases key
    /// </summary>
    private static object PhraseAnswer(object phrase) => new
    {
        version = "lexicon=x;sidecar=2",
        words = new[]
        {
            new { lemma = "気", pos = "名詞", isProper = false, reading = "き", jlpt = (int?) null, readings = new[] { "き" }, senses = Array.Empty<object>() },
            new { lemma = "する", pos = "動詞", isProper = false, reading = "する", jlpt = (int?) null, readings = new[] { "する" }, senses = Array.Empty<object>() },
            new { lemma = "気がする", pos = "表現", isProper = false, reading = "きがする", jlpt = (int?) null, readings = new[] { "きがする" }, senses = Array.Empty<object>() },
        },
        blocks = new object[]
        {
            new
            {
                id = "0",
                tokens = new object[]
                {
                    new object?[] { 0, 1, 0, "き", null }, new object?[] { 1, 2, null, null, null },
                    new object?[] { 2, 3, 1, "し", null }, new object?[] { 3, 4, null, null, null },
                },
                phrases = new[] { phrase },
            },
            new { id = "1", tokens = new object[] { new object?[] { 0, 1, 0, "き", null } } },
        },
    };

    [Fact]
    public async Task ProcessUnit_MapsPhraseWordIndicesToStoreIdsAndSavesTheirWords()
    {
        var (_, service, files) = await Setup();
        _pages.GetEpubPageBlocksAsync(files.Chapter1.FilePath, 0, Arg.Any<CancellationToken>())
            .Returns([Block(0, "気がした"), Block(1, "気")]);
        _httpTest.RespondWithJson(PhraseAnswer(new[] { 0, 3, 2 }));
        ReturnWordIds();

        var result = await service.ProcessUnitAsync(Unit(files.Chapter1), SidecarUrl);

        Assert.Equal(TokenizeUnitOutcome.UnitDone, result.Outcome);
        // No token points at 気がする, and it's saved with the rest
        await _store.Received(1).UpsertWordsAsync(Arg.Is<IReadOnlyList<SidecarWordDto>>(w => w.Count == 3 && w[2].Pos == "表現"),
            "x", Arg.Any<CancellationToken>());
        var (stored, _) = SavedUnit();
        Assert.Equal([new PhraseDto(0, 3, 102)], stored[0].Phrases);
        Assert.Empty(stored[1].Phrases);
    }

    [Theory]
    [InlineData(0, 1, 2)] // a single token
    [InlineData(0, 5, 2)] // ends past every token
    [InlineData(1, 1, 2)] // empty
    [InlineData(2, 0, 2)] // ends before it starts
    [InlineData(0, 3, 3)] // the answer has three words
    [InlineData(0, 3, -1)]
    public async Task ProcessUnit_BadPhrase_CountsAnAttemptAndSavesNothing(int s, int e, int w)
    {
        var (_, service, files) = await Setup();
        _pages.GetEpubPageBlocksAsync(files.Chapter1.FilePath, 0, Arg.Any<CancellationToken>())
            .Returns([Block(0, "気がした"), Block(1, "気")]);
        _httpTest.RespondWithJson(PhraseAnswer(new[] { s, e, w }));
        ReturnWordIds();

        var result = await service.ProcessUnitAsync(Unit(files.Chapter1), SidecarUrl);

        Assert.Equal(TokenizeUnitOutcome.UnitFailed, result.Outcome);
        await _store.Received(1).RecordFailedAttemptAsync(7, TokenizeService.MaxUnitAttempts, Arg.Any<CancellationToken>());
        await _store.DidNotReceiveWithAnyArgs().SaveTokenizedUnitAsync(default, default!, default!, default!);
    }

    [Fact]
    public async Task ProcessUnit_PageWithoutBlocks_IsSavedEmptyWithoutTheSidecar()
    {
        var (_, service, files) = await Setup();
        _pages.GetEpubPageBlocksAsync(files.Chapter1.FilePath, 0, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<EpubTextBlock>?) null);

        var result = await service.ProcessUnitAsync(Unit(files.Chapter1), SidecarUrl);

        Assert.Equal(TokenizeUnitOutcome.UnitDone, result.Outcome);
        Assert.Empty(_httpTest.CallLog);
        var (stored, _) = SavedUnit();
        Assert.Empty(stored);
    }

    [Fact]
    public async Task ProcessUnit_StoreRefusesTheSave_IsFileChanged()
    {
        var (_, service, files) = await Setup();
        _pages.GetEpubPageBlocksAsync(files.Chapter1.FilePath, 0, Arg.Any<CancellationToken>()).Returns(SampleBlocks());
        _httpTest.RespondWith(SampleResponse);
        ReturnWordIds();
        _store.SaveTokenizedUnitAsync(default, default!, default!, default!, default).ReturnsForAnyArgs(false);

        var result = await service.ProcessUnitAsync(Unit(files.Chapter1), SidecarUrl);

        Assert.Equal(TokenizeUnitOutcome.FileChanged, result.Outcome);
    }

    [Fact]
    public async Task ProcessUnit_SidecarTimesOut_IsUnavailableAndCountsNoAttempt()
    {
        var (_, service, files) = await Setup();
        _pages.GetEpubPageBlocksAsync(files.Chapter1.FilePath, 0, Arg.Any<CancellationToken>()).Returns(SampleBlocks());
        _httpTest.SimulateTimeout();

        var result = await service.ProcessUnitAsync(Unit(files.Chapter1), SidecarUrl);

        Assert.Equal(TokenizeUnitOutcome.SidecarUnavailable, result.Outcome);
        await _store.DidNotReceiveWithAnyArgs().RecordFailedAttemptAsync(default, default);
        await _store.DidNotReceiveWithAnyArgs().SaveTokenizedUnitAsync(default, default!, default!, default!);
    }

    [Fact]
    public async Task ProcessUnit_SidecarRejectsThePage_CountsAnAttempt()
    {
        var (_, service, files) = await Setup();
        _pages.GetEpubPageBlocksAsync(files.Chapter1.FilePath, 0, Arg.Any<CancellationToken>()).Returns(SampleBlocks());
        _httpTest.RespondWith("bad block", 422);

        var result = await service.ProcessUnitAsync(Unit(files.Chapter1), SidecarUrl);

        Assert.Equal(TokenizeUnitOutcome.UnitFailed, result.Outcome);
        await _store.Received(1).RecordFailedAttemptAsync(7, TokenizeService.MaxUnitAttempts, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessUnit_PageThatCantBeRead_CountsAnAttempt()
    {
        var (_, service, files) = await Setup();
        _pages.GetEpubPageBlocksAsync(files.Chapter1.FilePath, 0, Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("bad zip"));

        var result = await service.ProcessUnitAsync(Unit(files.Chapter1), SidecarUrl);

        Assert.Equal(TokenizeUnitOutcome.UnitFailed, result.Outcome);
        await _store.Received(1).RecordFailedAttemptAsync(7, TokenizeService.MaxUnitAttempts, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0, 99)] // past the end of the block's text
    [InlineData(3, 2)] // ends before it starts
    public async Task ProcessUnit_TokenOutsideItsBlock_CountsAnAttemptAndSavesNothing(int s, int e)
    {
        var (_, service, files) = await Setup();
        _pages.GetEpubPageBlocksAsync(files.Chapter1.FilePath, 0, Arg.Any<CancellationToken>()).Returns([Block(0, "上手に")]);
        _httpTest.RespondWithJson(new
        {
            version = "lexicon=x;sidecar=1",
            words = Array.Empty<object>(),
            blocks = new[] { new { id = "0", tokens = new object[] { new object?[] { s, e, null, null, null } } } },
        });

        var result = await service.ProcessUnitAsync(Unit(files.Chapter1), SidecarUrl);

        Assert.Equal(TokenizeUnitOutcome.UnitFailed, result.Outcome);
        await _store.Received(1).RecordFailedAttemptAsync(7, TokenizeService.MaxUnitAttempts, Arg.Any<CancellationToken>());
        await _store.DidNotReceiveWithAnyArgs().SaveTokenizedUnitAsync(default, default!, default!, default!);
    }

    [Theory]
    [InlineData(1, false)] // the answer has one word, so index 1 is past its end
    [InlineData(-1, false)]
    [InlineData(1, true)] // the same, in a part of a compound
    public async Task ProcessUnit_WordIndexOutsideTheAnswer_CountsAnAttemptAndSavesNothing(int w, bool inPart)
    {
        var (_, service, files) = await Setup();
        _pages.GetEpubPageBlocksAsync(files.Chapter1.FilePath, 0, Arg.Any<CancellationToken>()).Returns([Block(0, "上手に")]);
        _httpTest.RespondWithJson(new
        {
            version = "lexicon=x;sidecar=1",
            words = new[] { new { lemma = "上手", pos = "名詞", isProper = false, reading = "じょうず", jlpt = (int?) null, readings = new[] { "じょうず" }, senses = Array.Empty<object>() } },
            blocks = new[]
            {
                new
                {
                    id = "0",
                    tokens = new object[] { new object?[] { 0, 2, inPart ? 0 : w, null, inPart ? new object[] { new object?[] { 0, 1, w, null } } : null } },
                },
            },
        });

        var result = await service.ProcessUnitAsync(Unit(files.Chapter1), SidecarUrl);

        Assert.Equal(TokenizeUnitOutcome.UnitFailed, result.Outcome);
        await _store.DidNotReceiveWithAnyArgs().UpsertWordsAsync(default!, default!);
        await _store.DidNotReceiveWithAnyArgs().SaveTokenizedUnitAsync(default, default!, default!, default!);
    }

    [Fact]
    public async Task ProcessUnit_FileMissingFromDisk_WaitsWithoutCountingAnAttempt()
    {
        var (_, service, files) = await Setup();
        _fileSystem.RemoveFile(files.Chapter1.FilePath);

        var result = await service.ProcessUnitAsync(Unit(files.Chapter1), SidecarUrl);

        Assert.Equal(TokenizeUnitOutcome.FileUnavailable, result.Outcome);
        await _store.Received(1).DeferUnitAsync(7, Arg.Any<CancellationToken>());
        await _pages.DidNotReceiveWithAnyArgs().GetEpubPageBlocksAsync(default!, default);
        await _store.DidNotReceiveWithAnyArgs().RecordFailedAttemptAsync(default, default);
        await _store.DidNotReceiveWithAnyArgs().SaveTokenizedUnitAsync(default, default!, default!, default!);
        await _store.DidNotReceiveWithAnyArgs().DeleteFilesAsync(default!);
    }

    [Fact]
    public async Task ProcessUnit_ChangedFileThatCantBeReadThisTime_WaitsWithoutTouchingItsUnits()
    {
        var (_, service, files) = await Setup();
        _fileSystem.GetFile(files.Chapter1.FilePath).LastWriteTime = WriteTime.AddHours(1);
        _pages.GetEpubPageCountAsync(files.Chapter1.FilePath, Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("locked"));

        var result = await service.ProcessUnitAsync(Unit(files.Chapter1), SidecarUrl);

        Assert.Equal(TokenizeUnitOutcome.FileUnavailable, result.Outcome);
        await _store.Received(1).DeferUnitAsync(7, Arg.Any<CancellationToken>());
        await _store.DidNotReceiveWithAnyArgs().SeedFileAsync(default, default, default!, default);
        await _store.DidNotReceiveWithAnyArgs().DeleteFilesAsync(default!);
        await _store.DidNotReceiveWithAnyArgs().RecordFailedAttemptAsync(default, default);
    }

    [Fact]
    public async Task ProcessUnit_TokensOutOfOrder_CountsAnAttempt()
    {
        var (_, service, files) = await Setup();
        _pages.GetEpubPageBlocksAsync(files.Chapter1.FilePath, 0, Arg.Any<CancellationToken>()).Returns([Block(0, "上手に店")]);
        _httpTest.RespondWithJson(new
        {
            version = "lexicon=x;sidecar=1",
            words = Array.Empty<object>(),
            blocks = new[] { new { id = "0", tokens = new object[] { new object?[] { 2, 3, null, null, null }, new object?[] { 0, 2, null, null, null } } } },
        });

        var result = await service.ProcessUnitAsync(Unit(files.Chapter1), SidecarUrl);

        Assert.Equal(TokenizeUnitOutcome.UnitFailed, result.Outcome);
        await _store.DidNotReceiveWithAnyArgs().SaveTokenizedUnitAsync(default, default!, default!, default!);
    }

    [Fact]
    public async Task ProcessUnit_FileChangedOnDisk_SeedsItAgain()
    {
        var (_, service, files) = await Setup();
        _fileSystem.GetFile(files.Chapter1.FilePath).LastWriteTime = WriteTime.AddHours(1);

        var result = await service.ProcessUnitAsync(Unit(files.Chapter1), SidecarUrl);

        Assert.Equal(TokenizeUnitOutcome.FileChanged, result.Outcome);
        await _store.Received(1).SeedFileAsync(files.Chapter1.Id, PageCount, NewerStamp, ReadingPriority.WholeBook, Arg.Any<CancellationToken>());
        Assert.Empty(_httpTest.CallLog);
    }

    [Fact]
    public async Task ProcessUnit_FileNoLongerEligible_DeletesItsUnits()
    {
        var (_, service, files) = await Setup();

        var result = await service.ProcessUnitAsync(Unit(files.Disabled), SidecarUrl);

        Assert.Equal(TokenizeUnitOutcome.FileChanged, result.Outcome);
        await _store.Received(1).DeleteFilesAsync(Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { files.Disabled.Id })),
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region Priorities

    [Fact]
    public async Task PrioritizeChapter_RaisesTheChapterAndTheNextOne()
    {
        var (_, service, files) = await Setup();

        var result = await service.PrioritizeChapterAsync(files.FirstChapter.Id);

        Assert.Equal([files.Chapter1.Id], result.OpenChapterFileIds);
        Assert.Equal([files.Chapter2.Id], result.NextChapterFileIds);
        // Neither file had units, so they're seeded at their priorities as well as raised
        await _store.Received(1).SeedFileAsync(files.Chapter1.Id, PageCount, Stamp, ReadingPriority.OpenChapter, Arg.Any<CancellationToken>());
        await _store.Received(1).SeedFileAsync(files.Chapter2.Id, PageCount, Stamp, ReadingPriority.NextChapter, Arg.Any<CancellationToken>());
        await _store.Received(1).RaiseUnitPriorityAsync(files.Chapter1.Id, null, ReadingPriority.OpenChapter, Arg.Any<CancellationToken>());
        await _store.Received(1).RaiseUnitPriorityAsync(files.Chapter2.Id, null, ReadingPriority.NextChapter, Arg.Any<CancellationToken>());
        _signal.Received().Wake();
    }

    [Fact]
    public async Task PrioritizeChapter_LastChapter_HasNoNextFiles()
    {
        var (_, service, files) = await Setup();
        _store.GetFileStampsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<int, string> { [files.Chapter2.Id] = Stamp });

        var result = await service.PrioritizeChapterAsync(files.SecondChapter.Id);

        // The PDF in the same chapter isn't tokenized
        Assert.Equal([files.Chapter2.Id], result.OpenChapterFileIds);
        Assert.Empty(result.NextChapterFileIds);
        await _store.DidNotReceiveWithAnyArgs().SeedFileAsync(default, default, default!, default);
    }

    [Fact]
    public async Task PrioritizeChapter_ChapterThatIsntJapanese_RaisesNothing()
    {
        var (context, service, files) = await Setup();
        var draculaChapter = context.MangaFile.Single(f => f.Id == files.English.Id).ChapterId;

        var result = await service.PrioritizeChapterAsync(draculaChapter);

        Assert.Empty(result.OpenChapterFileIds);
        await _store.DidNotReceiveWithAnyArgs().RaiseUnitPriorityAsync(default, default, default);
        _signal.DidNotReceive().Wake();
    }

    #endregion

    [Fact]
    public void LexiconVersion_OfTheRealSample_KeepsItsSemicolons()
    {
        var sample = JsonSerializer.Deserialize<TokenizeResponseDto>(SampleResponse)!;

        Assert.Equal("jmdict=3.6.2/2026-11-16;kanjidic=2026-320;jmnedict=3.6.2/2026-11-16;" +
                     "jlpt=b062d4e38c4bdd0950ae1d4ec55f04b176182e03;" +
                     "freq=bccwj_yomitan_rank/Kuuuube/yomitan-dictionaries@654b0f71ae51d27e888cc621c069591846927100",
            TokenizeService.LexiconVersion(sample.Version));
        Assert.Equal(string.Empty, TokenizeService.LexiconVersion("sudachipy=1;sidecar=1"));
    }

    [Theory]
    [InlineData("ja", true)]
    [InlineData("JA-jp", true)]
    [InlineData("ja_JP", true)]
    [InlineData("jpn", true)]
    [InlineData(" ja ", true)]
    [InlineData("jam", false)]
    [InlineData("en", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsJapanese(string? language, bool expected)
    {
        Assert.Equal(expected, TokenizeService.IsJapanese(language));
    }
}
