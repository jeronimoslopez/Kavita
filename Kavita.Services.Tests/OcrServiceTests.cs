using System.IO.Abstractions.TestingHelpers;
using Flurl.Http.Testing;
using Kavita.API.Database;
using Kavita.API.Services;
using Kavita.API.Services.Scanner;
using Kavita.API.Services.SignalR;
using Kavita.Common;
using Kavita.Database;
using Kavita.Database.Tests;
using Kavita.Models.Builders;
using Kavita.Models.Entities;
using Kavita.Models.Entities.Enums;
using Kavita.Services.Builders;
using Kavita.Services.Ocr;
using Kavita.Services.Scanner;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit.Abstractions;

namespace Kavita.Services.Tests;

public class OcrServiceTests(ITestOutputHelper outputHelper) : AbstractDbTest(outputHelper), IDisposable
{
    private const string PdfPath = "/books/Quartet/Quartet 1.pdf";
    private const string SidecarUrl = "http://ocr:8000";
    private static readonly DateTime WriteTime = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    /// <summary>
    /// A real response the sidecar gave for a vertical page with furigana. Its image is 1100x1500
    /// </summary>
    private static readonly string SampleResponse = File.ReadAllText(
        Path.Join(Directory.GetCurrentDirectory(), "../../../../ocr-sidecar/contract/sample-response.json"));

    private readonly IBookService _bookService = Substitute.For<IBookService>();
    private readonly IOcrWorkerSignal _workerSignal = Substitute.For<IOcrWorkerSignal>();
    private readonly MockFileSystem _fileSystem = new();
    private readonly HttpTest _httpTest = new();

    public void Dispose() => _httpTest.Dispose();

    private async Task<(IUnitOfWork, DataContext, OcrService, MangaFile)> Setup(bool enableOcr = true)
    {
        var (unitOfWork, context, _) = await CreateDatabase();
        _fileSystem.AddFile(PdfPath, new MockFileData("pdf") { LastWriteTime = WriteTime });
        _bookService.RenderPdfPageForOcr(Arg.Any<string>(), Arg.Any<int>()).Returns(([1, 2, 3], 1100, 1500));

        var file = new MangaFileBuilder(PdfPath, MangaFormat.Pdf, 3).Build();
        var library = new LibraryBuilder("Textbooks", LibraryType.Book).WithSeries(BuildSeries("Quartet", file)).Build();
        library.EnableOcr = enableOcr;
        context.Library.Add(library);
        await context.SaveChangesAsync();

        var service = new OcrService(Substitute.For<ILogger<OcrService>>(), unitOfWork, _bookService,
            new DirectoryService(Substitute.For<ILogger<DirectoryService>>(), _fileSystem), new OcrSidecarClient(),
            Substitute.For<IEventHub>(), _workerSignal);
        return (unitOfWork, context, service, file);
    }

    private static Series BuildSeries(string name, MangaFile file)
    {
        return new SeriesBuilder(name)
            .WithFormat(MangaFormat.Pdf)
            .WithVolume(new VolumeBuilder(Parser.LooseLeafVolume)
                .WithChapter(new ChapterBuilder("1").WithFile(file).Build())
                .Build())
            .Build();
    }

    private static async Task QueuePages(DataContext context, MangaFile file, params int[] pages)
    {
        file.OcrStatus = OcrStatus.Pending;
        file.OcrSourceWriteTimeUtc = WriteTime;
        foreach (var page in pages)
        {
            context.OcrPage.Add(new OcrPage { MangaFileId = file.Id, PageNumber = page, State = OcrPageState.Pending });
        }
        await context.SaveChangesAsync();
    }

    #region Detection

    [Fact]
    public async Task DetectNewFiles_QueuesOnlyPagesWithoutText()
    {
        var (_, context, service, file) = await Setup();
        _bookService.GetPdfPageTextLengths(PdfPath).Returns([0, 120, OcrService.MinCharactersForText - 1]);

        await service.DetectNewFilesAsync();

        await context.Entry(file).ReloadAsync();
        Assert.Equal(OcrStatus.Pending, file.OcrStatus);
        Assert.Equal(WriteTime, file.OcrSourceWriteTimeUtc);
        Assert.NotNull(file.OcrRequestedUtc);
        var pages = await context.OcrPage.Where(p => p.MangaFileId == file.Id).OrderBy(p => p.PageNumber).ToListAsync();
        Assert.Equal([0, 2], pages.Select(p => p.PageNumber));
        Assert.All(pages, p => Assert.Equal(OcrPageState.Pending, p.State));
        _workerSignal.Received().Wake();
    }

    [Fact]
    public async Task DetectNewFiles_FileWithTextOnEveryPage_IsLeftAlone()
    {
        var (_, context, service, file) = await Setup();
        _bookService.GetPdfPageTextLengths(PdfPath).Returns([400, 380, 12]);

        await service.DetectNewFilesAsync();

        await context.Entry(file).ReloadAsync();
        Assert.Equal(OcrStatus.HasText, file.OcrStatus);
        Assert.Empty(context.OcrPage);
    }

    [Fact]
    public async Task DetectNewFiles_UnreadablePdf_IsMarkedFailed()
    {
        var (_, context, service, file) = await Setup();
        _bookService.GetPdfPageTextLengths(PdfPath).Returns(_ => throw new InvalidOperationException("broken xref"));

        await service.DetectNewFilesAsync();

        await context.Entry(file).ReloadAsync();
        Assert.Equal(OcrStatus.Failed, file.OcrStatus);
    }

    [Fact]
    public async Task DetectNewFiles_LibraryWithoutOcr_IsNotChecked()
    {
        var (_, context, service, file) = await Setup(enableOcr: false);

        await service.DetectNewFilesAsync();

        await context.Entry(file).ReloadAsync();
        Assert.Equal(OcrStatus.NotChecked, file.OcrStatus);
        _bookService.DidNotReceiveWithAnyArgs().GetPdfPageTextLengths(default!);
    }

    [Fact]
    public async Task MarkSeriesForCheck_ChangedFile_DropsItsOcrAndGoesBackToNotChecked()
    {
        var (_, context, service, file) = await Setup();
        file.OcrStatus = OcrStatus.Done;
        file.OcrSourceWriteTimeUtc = WriteTime.AddDays(-1);
        context.OcrPage.Add(new OcrPage { MangaFileId = file.Id, PageNumber = 0, State = OcrPageState.Done });
        await context.SaveChangesAsync();

        await service.MarkSeriesForCheckAsync(1);

        await context.Entry(file).ReloadAsync();
        Assert.Equal(OcrStatus.NotChecked, file.OcrStatus);
        Assert.Empty(context.OcrPage);
    }

    [Fact]
    public async Task MarkSeriesForCheck_UnchangedFile_KeepsItsOcr()
    {
        var (_, context, service, file) = await Setup();
        file.OcrStatus = OcrStatus.Done;
        file.OcrSourceWriteTimeUtc = WriteTime;
        context.OcrPage.Add(new OcrPage { MangaFileId = file.Id, PageNumber = 0, State = OcrPageState.Done });
        await context.SaveChangesAsync();

        await service.MarkSeriesForCheckAsync(1);

        await context.Entry(file).ReloadAsync();
        Assert.Equal(OcrStatus.Done, file.OcrStatus);
        Assert.Single(context.OcrPage);
    }

    [Fact]
    public async Task MarkSeriesForCheck_RemovedFile_StaysRemovedAfterItChanges()
    {
        var (_, context, service, file) = await Setup();
        file.OcrStatus = OcrStatus.Removed;
        file.OcrSourceWriteTimeUtc = WriteTime.AddDays(-1);
        await context.SaveChangesAsync();

        await service.MarkSeriesForCheckAsync(1);

        await context.Entry(file).ReloadAsync();
        Assert.Equal(OcrStatus.Removed, file.OcrStatus);
    }

    #endregion

    #region Processing

    [Fact]
    public async Task ProcessNextPage_StoresLinesAsFractionsOfThePage()
    {
        var (_, context, service, file) = await Setup();
        await QueuePages(context, file, 1);
        _httpTest.RespondWith(SampleResponse);

        var result = await service.ProcessNextPageAsync(file.Id, SidecarUrl);

        Assert.Equal(OcrPageOutcome.PageDone, result.Outcome);
        Assert.Equal(2, result.PageNum);
        _httpTest.ShouldHaveCalled($"{SidecarUrl}/ocr").WithVerb(HttpMethod.Post);

        var page = await context.OcrPage.AsNoTracking().SingleAsync();
        Assert.Equal(OcrPageState.Done, page.State);
        Assert.Equal("ndlocr-lite", page.Engine);
        Assert.Equal(1100, page.ImageWidth);
        var first = page.Lines[0];
        Assert.True(first.Vertical);
        Assert.Equal(OcrLineKind.Body, first.Kind);
        Assert.Equal(957 / 1100f, first.X, 4);
        Assert.Equal(1251 / 1500f, first.Height, 4);
        Assert.Equal(OcrLineKind.Ruby, page.Lines[1].Kind);
        Assert.Equal(page.Lines.Select(l => l.Order).Order(), page.Lines.Select(l => l.Order));

        await context.Entry(file).ReloadAsync();
        Assert.Equal(OcrStatus.Processing, file.OcrStatus);
    }

    [Fact]
    public async Task ProcessNextPage_ResumesAfterTheDonePages()
    {
        var (_, context, service, file) = await Setup();
        await QueuePages(context, file, 2);
        context.OcrPage.Add(new OcrPage { MangaFileId = file.Id, PageNumber = 0, State = OcrPageState.Done });
        await context.SaveChangesAsync();
        _httpTest.RespondWith(SampleResponse);

        var result = await service.ProcessNextPageAsync(file.Id, SidecarUrl);

        Assert.Equal(3, result.PageNum);
        _bookService.Received(1).RenderPdfPageForOcr(PdfPath, 2);
    }

    [Fact]
    public async Task ProcessNextPage_NoPagesLeft_FinishesTheFile()
    {
        var (_, context, service, file) = await Setup();
        await QueuePages(context, file);
        file.OcrPriority = OcrService.ReaderPriority;
        context.OcrPage.Add(new OcrPage { MangaFileId = file.Id, PageNumber = 0, State = OcrPageState.Done });
        await context.SaveChangesAsync();

        var result = await service.ProcessNextPageAsync(file.Id, SidecarUrl);

        Assert.Equal(OcrPageOutcome.FileFinished, result.Outcome);
        await context.Entry(file).ReloadAsync();
        Assert.Equal(OcrStatus.Done, file.OcrStatus);
        Assert.Equal(0, file.OcrPriority);
    }

    [Fact]
    public async Task ProcessNextPage_SidecarUnreachable_ChangesNothing()
    {
        var (_, context, service, file) = await Setup();
        await QueuePages(context, file, 0);
        _httpTest.SimulateException(new HttpRequestException("Connection refused"));

        var result = await service.ProcessNextPageAsync(file.Id, SidecarUrl);

        Assert.Equal(OcrPageOutcome.SidecarUnavailable, result.Outcome);
        var page = await context.OcrPage.AsNoTracking().SingleAsync();
        Assert.Equal(OcrPageState.Pending, page.State);
        Assert.Equal(0, page.Attempts);
    }

    [Fact]
    public async Task SidecarClient_CancelledRequest_IsReportedAsCancelled()
    {
        // The fake doesn't watch the token, so stand in for the request HttpClient abandons on shutdown
        _httpTest.SimulateException(new TaskCanceledException());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OcrSidecarClient().RecognizeAsync(SidecarUrl, [1], cts.Token));
    }

    [Fact]
    public async Task ProcessNextPage_RejectedThreeTimes_FailsThePageAndThenTheFile()
    {
        var (_, context, service, file) = await Setup();
        await QueuePages(context, file, 0);
        _httpTest.RespondWith("could not decode image", 400);

        for (var attempt = 1; attempt <= OcrService.MaxPageAttempts; attempt++)
        {
            var result = await service.ProcessNextPageAsync(file.Id, SidecarUrl);
            Assert.Equal(OcrPageOutcome.PageFailed, result.Outcome);
        }

        var page = await context.OcrPage.AsNoTracking().SingleAsync();
        Assert.Equal(OcrPageState.Failed, page.State);
        Assert.Equal(OcrService.MaxPageAttempts, page.Attempts);

        Assert.Equal(OcrPageOutcome.FileFinished, (await service.ProcessNextPageAsync(file.Id, SidecarUrl)).Outcome);
        await context.Entry(file).ReloadAsync();
        Assert.Equal(OcrStatus.Failed, file.OcrStatus);
    }

    [Fact]
    public async Task ProcessNextPage_FileReplaced_DropsItsOcrAndChecksItAgain()
    {
        var (_, context, service, file) = await Setup();
        await QueuePages(context, file, 0, 1);
        _fileSystem.File.SetLastWriteTimeUtc(PdfPath, WriteTime.AddHours(1));

        var result = await service.ProcessNextPageAsync(file.Id, SidecarUrl);

        Assert.Equal(OcrPageOutcome.FileChanged, result.Outcome);
        Assert.Empty(context.OcrPage);
        await context.Entry(file).ReloadAsync();
        Assert.Equal(OcrStatus.NotChecked, file.OcrStatus);
        Assert.Empty(_httpTest.CallLog);
    }

    [Fact]
    public async Task GetNextFileId_HigherPriorityFirst_ThenOldestRequest()
    {
        var (_, context, service, file) = await Setup();
        await QueuePages(context, file, 0);
        file.OcrRequestedUtc = WriteTime;

        const string otherPath = "/books/Genki/Genki 1.pdf";
        _fileSystem.AddFile(otherPath, new MockFileData("pdf") { LastWriteTime = WriteTime });
        var other = new MangaFileBuilder(otherPath, MangaFormat.Pdf, 3).Build();
        var library = await context.Library.Include(l => l.Series).SingleAsync(l => l.Name == "Textbooks");
        library.Series.Add(BuildSeries("Genki", other));
        await context.SaveChangesAsync();
        await QueuePages(context, other, 0);
        other.OcrRequestedUtc = WriteTime.AddMinutes(5);
        await context.SaveChangesAsync();

        Assert.Equal(file.Id, await service.GetNextFileIdAsync());

        other.OcrPriority = OcrService.ReaderPriority;
        await context.SaveChangesAsync();
        Assert.Equal(other.Id, await service.GetNextFileIdAsync());
    }

    #endregion

    #region Reader and admin

    [Fact]
    public async Task GetPage_UsesOneBasedPageNumbers()
    {
        var (_, context, service, file) = await Setup();
        context.OcrPage.Add(new OcrPage
        {
            MangaFileId = file.Id, PageNumber = 0, State = OcrPageState.Done,
            Lines = [new OcrLine { Text = "吾輩は猫である。", Vertical = true }],
        });
        await context.SaveChangesAsync();

        var page = await service.GetPageAsync(file.ChapterId, 1);
        var status = await service.GetChapterStatusAsync(file.ChapterId);

        Assert.NotNull(page);
        Assert.Equal("吾輩は猫である。", Assert.Single(page.Lines).Text);
        Assert.Null(await service.GetPageAsync(file.ChapterId, 2));
        Assert.Equal([1], status!.Pages);
    }

    [Fact]
    public async Task GetChapterPages_ReturnsOnlyDonePagesOneBasedInOrder()
    {
        var (_, context, service, file) = await Setup();
        context.OcrPage.AddRange(
            new OcrPage { MangaFileId = file.Id, PageNumber = 2, State = OcrPageState.Done, Lines = [new OcrLine { Text = "三" }] },
            new OcrPage { MangaFileId = file.Id, PageNumber = 1, State = OcrPageState.Failed },
            new OcrPage { MangaFileId = file.Id, PageNumber = 0, State = OcrPageState.Done, Lines = [new OcrLine { Text = "一" }] });
        await context.SaveChangesAsync();

        var pages = await service.GetChapterPagesAsync(file.ChapterId);

        Assert.NotNull(pages);
        Assert.Equal([1, 3], pages.Select(p => p.PageNumber));
        Assert.Equal("三", Assert.Single(pages[1].Lines).Text);
    }

    [Fact]
    public async Task GetChapterPages_ChapterWithoutPdf_ReturnsNull()
    {
        var (_, _, service, _) = await Setup();

        Assert.Null(await service.GetChapterPagesAsync(999));
    }

    [Fact]
    public async Task PrioritizeChapter_RaisesAQueuedFile()
    {
        var (_, context, service, file) = await Setup();
        await QueuePages(context, file, 0);

        await service.PrioritizeChapterAsync(file.ChapterId);

        await context.Entry(file).ReloadAsync();
        Assert.Equal(OcrService.ReaderPriority, file.OcrPriority);
        _workerSignal.Received().Wake();
    }

    [Fact]
    public async Task RunSeries_ReplacingText_QueuesEveryPage()
    {
        var (_, context, service, file) = await Setup();
        file.OcrStatus = OcrStatus.HasText;
        file.OcrSourceWriteTimeUtc = WriteTime;
        await context.SaveChangesAsync();

        Assert.Equal(0, await service.RunSeriesAsync(1, replaceExistingText: false));
        Assert.Equal(1, await service.RunSeriesAsync(1, replaceExistingText: true));

        await context.Entry(file).ReloadAsync();
        Assert.Equal(OcrStatus.Pending, file.OcrStatus);
        Assert.Equal(OcrService.AdminPriority, file.OcrPriority);
        Assert.Equal([0, 1, 2], context.OcrPage.OrderBy(p => p.PageNumber).Select(p => p.PageNumber));
    }

    [Fact]
    public async Task RemoveSeries_DeletesTheTextAndMarksTheFileRemoved()
    {
        var (_, context, service, file) = await Setup();
        await QueuePages(context, file, 0, 1);

        await service.RemoveSeriesAsync(1);

        Assert.Empty(context.OcrPage);
        await context.Entry(file).ReloadAsync();
        Assert.Equal(OcrStatus.Removed, file.OcrStatus);
    }

    #endregion

    #region Settings

    [Theory]
    [InlineData("", "", "03:00", true)]
    [InlineData("01:00", "07:00", "03:00", true)]
    [InlineData("01:00", "07:00", "07:00", false)]
    [InlineData("22:00", "06:00", "23:30", true)]
    [InlineData("22:00", "06:00", "05:59", true)]
    [InlineData("22:00", "06:00", "12:00", false)]
    [InlineData("09:00", "09:00", "15:00", true)]
    [InlineData("9am", "17:00", "20:00", true)]
    public void IsWithinWindow(string start, string end, string now, bool expected)
    {
        Assert.Equal(expected, OcrSchedule.IsWithinWindow(TimeOnly.Parse(now), start, end));
    }

    [Fact]
    public async Task UpdateSettings_StoresTheSidecarUrlAndRejectsBadValues()
    {
        var (unitOfWork, _, _) = await CreateDatabase();
        var settingsService = new SettingsService(unitOfWork, Substitute.For<IDirectoryService>(), Substitute.For<ILibraryWatcher>(),
            Substitute.For<ITaskScheduler>(), Substitute.For<ILogger<SettingsService>>(), Substitute.For<IOidcService>(),
            Substitute.For<ILoggingService>());

        var settings = await unitOfWork.SettingsRepository.GetSettingsDtoAsync();
        Assert.Equal(string.Empty, settings.OcrSidecarUrl);
        settings.OcrSidecarUrl = " http://ocr:8000/ ";
        settings.OcrWindowStart = "01:00";
        settings.OcrWindowEnd = "07:00";
        await settingsService.UpdateSettings(settings);

        var saved = await unitOfWork.SettingsRepository.GetSettingsDtoAsync();
        Assert.Equal("http://ocr:8000", saved.OcrSidecarUrl);
        Assert.Equal("01:00", saved.OcrWindowStart);

        saved.OcrSidecarUrl = "ocr:8000";
        await Assert.ThrowsAsync<KavitaException>(() => settingsService.UpdateSettings(saved));
        saved.OcrSidecarUrl = "http://ocr:8000";
        saved.OcrWindowEnd = "25:00";
        await Assert.ThrowsAsync<KavitaException>(() => settingsService.UpdateSettings(saved));
    }

    #endregion
}
