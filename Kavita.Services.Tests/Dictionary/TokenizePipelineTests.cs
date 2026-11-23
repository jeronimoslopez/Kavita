using System.IO.Abstractions;
using System.Text.Json;
using Flurl.Http.Testing;
using Kavita.API.Database;
using Kavita.API.Services;
using Kavita.API.Services.Plus;
using Kavita.API.Services.Reading;
using Kavita.API.Services.SignalR;
using Kavita.Database.Tests;
using Kavita.Models.Builders;
using Kavita.Models.DTOs.Dictionary;
using Kavita.Models.Entities;
using Kavita.Models.Entities.Enums;
using Kavita.Services.Builders;
using Kavita.Services.Dictionary;
using Kavita.Services.HostedServices;
using Kavita.Services.Reading;
using Kavita.Services.Scanner;
using Kavita.Services.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit.Abstractions;

namespace Kavita.Services.Tests.Dictionary;

/// <summary>
/// Runs the tokenizer through the real <see cref="BookService"/> and <see cref="ReadingStore"/> on EPUBs written to
/// disk, with only the sidecar faked
/// </summary>
[Collection(HangfireStorageCollection.Name)]
public class TokenizePipelineTests(ITestOutputHelper outputHelper) : AbstractDbTest(outputHelper), IDisposable
{
    private const string SidecarUrl = "http://text:8000";

    private static readonly string FirstPage = """
        <?xml version="1.0" encoding="UTF-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml" xml:lang="ja">
        <head><title>一</title></head>
        <body>
        <h1>第一章</h1>
        <p><ruby>吾輩<rt>わがはい</rt></ruby>は猫である。</p>
        <p><br/></p>
        <p>名前はまだ&#x7121;い。</p>
        </body>
        </html>
        """.ReplaceLineEndings("\n");

    private static readonly string SecondPage = """
        <?xml version="1.0" encoding="UTF-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml" xml:lang="ja">
        <head><title>二</title></head>
        <body><div><p>どこで<ruby>生<rt>うま</rt></ruby>れたか。</p><blockquote><p>見当がつかぬ。</p></blockquote></div></body>
        </html>
        """.ReplaceLineEndings("\n");

    private readonly string _directory = Path.Join(Path.GetTempPath(), "kavita-tokenize-pipeline", Guid.NewGuid().ToString("N"));
    private readonly List<EpubFixture> _books = [];
    private readonly HttpTest _httpTest = new();
    private readonly BookService _bookService = CreateBookService();
    private ReadingStore _store = null!;

    public void Dispose()
    {
        _httpTest.Dispose();
        foreach (var book in _books) book.Dispose();
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

    private static BookService CreateBookService()
    {
        var directoryService = new DirectoryService(Substitute.For<ILogger<DirectoryService>>(), new FileSystem());
        return new BookService(Substitute.For<ILogger<BookService>>(), directoryService,
            new ImageService(Substitute.For<ILogger<ImageService>>(), directoryService),
            Substitute.For<IMediaErrorService>(), Substitute.For<IUnitOfWork>(), new EpubTextBlocks());
    }

    /// <summary>
    /// A Japanese dictionary library with one series per book, each book a chapter of its own
    /// </summary>
    private async Task<(IUnitOfWork, TokenizeService, List<MangaFile>)> Setup(params string[][] books)
    {
        var (unitOfWork, context, _) = await CreateDatabase();
        TokenizeService.ForgetUnusableFiles();
        _store = new ReadingStore(new ReadingDatabase(Path.Join(_directory, "reading.db")));

        var libraryBuilder = new LibraryBuilder("Books", LibraryType.Book);
        var files = new List<MangaFile>();
        for (var i = 0; i < books.Length; i++)
        {
            var book = new EpubFixture("ja", "p { margin: 0; }", books[i]);
            _books.Add(book);
            var file = new MangaFileBuilder(book.FilePath, MangaFormat.Epub, books[i].Length).Build();
            files.Add(file);
            var chapter = new ChapterBuilder("1").WithFile(file).Build();
            chapter.Language = "ja";
            libraryBuilder.WithSeries(new SeriesBuilder($"Book {i}")
                .WithFormat(MangaFormat.Epub)
                .WithVolume(new VolumeBuilder(Parser.LooseLeafVolume).WithChapter(chapter).Build())
                .Build());
        }

        var library = libraryBuilder.Build();
        library.EnableDictionary = true;
        context.Library.Add(library);
        context.ServerSetting.Single(s => s.Key == ServerSettingKey.TextSidecarUrl).Value = SidecarUrl;
        await context.SaveChangesAsync();

        var service = new TokenizeService(Substitute.For<ILogger<TokenizeService>>(), unitOfWork, _bookService,
            new DirectoryService(Substitute.For<ILogger<DirectoryService>>(), new FileSystem()), _store,
            new TextSidecarClient(), Substitute.For<ITokenizeWorkerSignal>(), CreateReaderService(unitOfWork));
        return (unitOfWork, service, files);
    }

    private static ReaderService CreateReaderService(IUnitOfWork unitOfWork)
    {
        return new ReaderService(unitOfWork, Substitute.For<ILogger<ReaderService>>(),
            Substitute.For<IEventHub>(), Substitute.For<IImageService>(),
            new DirectoryService(Substitute.For<ILogger<DirectoryService>>(), new FileSystem()),
            Substitute.For<IScrobblingService>(), Substitute.For<IReadingSessionService>(),
            Substitute.For<IClientInfoAccessor>(), Substitute.For<IEntityNamingService>(),
            Substitute.For<ILocalizationService>(), Substitute.For<IBookService>());
    }

    /// <summary>
    /// What the sidecar would answer for the page, with one token over the first character of each block that has
    /// text, all pointing at a word with two senses so each gets an analysis row
    /// </summary>
    private async Task<string> SidecarAnswerAsync(string filePath, int page)
    {
        var blocks = await _bookService.GetEpubPageBlocksAsync(filePath, page) ?? [];
        var answer = new
        {
            version = "sudachipy=0.6;modes=C/A;words=6;lexicon=test;sidecar=1",
            words = new[]
            {
                new
                {
                    lemma = "吾輩", pos = "代名詞", isProper = false, reading = "わがはい", jlpt = (int?) 1,
                    readings = new[] { "わがはい" },
                    senses = new[]
                    {
                        new { key = "1:1", pos = new[] { "pn" }, gloss = new[] { "I" } },
                        new { key = "1:2", pos = new[] { "pn" }, gloss = new[] { "me" } },
                    },
                },
            },
            blocks = blocks.Select(b => new
            {
                id = b.Index.ToString(),
                tokens = b.Text.Length == 0 ? [] : new[] { new object?[] { 0, 1, 0, null, null } },
            }),
        };
        return JsonSerializer.Serialize(answer);
    }

    [Fact]
    public async Task ProcessUnit_SavesTheBlocksBookServiceReads()
    {
        var (_, service, files) = await Setup([FirstPage, SecondPage]);
        var path = files[0].FilePath;

        await service.DetectNewFilesAsync();
        Assert.Equal(2, (await _store.GetCountsAsync(null)).UnitsPending);

        for (var page = 0; page < 2; page++)
        {
            _httpTest.RespondWith(await SidecarAnswerAsync(path, page));
            var unit = (await service.GetNextUnitAsync())!;
            Assert.Equal(page, unit.Page);

            var result = await service.ProcessUnitAsync(unit, SidecarUrl);

            Assert.Equal(TokenizeUnitOutcome.UnitDone, result.Outcome);
            var expected = (await _bookService.GetEpubPageBlocksAsync(path, page))!;
            var saved = (await _store.GetUnitAsync(files[0].Id, page))!;
            Assert.Equal(ReadingUnitState.Done, saved.State);
            Assert.Equal(expected.Select(b => (b.Index, b.Text)), saved.Blocks.Select(b => (b.Index, b.Text)));
            Assert.All(saved.Blocks, b => Assert.Equal(b.Text.Length == 0 ? 0 : 1, b.Tokens.Count));
            Assert.Equal(expected.Count(b => b.Text.Length > 0), (await _store.GetUnitAnalysisAsync(saved.Id)).Count);
        }

        // The ruby text stays out of the blocks, as the reader's walk leaves it out
        var first = (await _store.GetUnitAsync(files[0].Id, 0))!;
        Assert.Contains(first.Blocks, b => b.Text == "吾輩は猫である。");
        Assert.Null(await service.GetNextUnitAsync());
    }

    [Fact]
    public async Task WorkerPass_MissingFile_TokenizesTheUnitsBehindIt()
    {
        var (unitOfWork, service, files) = await Setup([FirstPage], [SecondPage], [FirstPage]);
        await service.DetectNewFilesAsync();
        var missing = (await service.GetNextUnitAsync())!;
        Assert.Equal(files[0].Id, missing.MangaFileId);
        File.Delete(files[0].FilePath);

        _httpTest.RespondWith(await SidecarAnswerAsync(files[1].FilePath, 0));
        _httpTest.RespondWith(await SidecarAnswerAsync(files[2].FilePath, 0));

        var services = new ServiceCollection();
        services.AddSingleton(unitOfWork);
        services.AddSingleton<ITokenizeService>(service);
        services.AddSingleton(Substitute.For<IEventHub>());
        var worker = new TokenizeWorkerHostedService(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<ILogger<TokenizeWorkerHostedService>>());

        Assert.False(await worker.RunPassAsync(CancellationToken.None));

        Assert.Equal(ReadingUnitState.Done, (await _store.GetUnitAsync(files[1].Id, 0))!.State);
        Assert.Equal(ReadingUnitState.Done, (await _store.GetUnitAsync(files[2].Id, 0))!.State);
        var waiting = (await _store.GetNextPendingUnitAsync())!;
        Assert.Equal(missing.Id, waiting.Id);
        Assert.Equal(0, waiting.Attempts);
        Assert.Equal(2, _httpTest.CallLog.Count);
    }
}
