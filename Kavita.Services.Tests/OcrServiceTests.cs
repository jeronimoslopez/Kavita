using System.IO.Abstractions.TestingHelpers;
using Kavita.API.Database;
using Kavita.API.Services;
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

public class OcrServiceTests(ITestOutputHelper outputHelper) : AbstractDbTest(outputHelper)
{
    private const string PdfPath = "/books/Quartet/Quartet 1.pdf";
    private static readonly DateTime WriteTime = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly IBookService _bookService = Substitute.For<IBookService>();
    private readonly MockFileSystem _fileSystem = new();

    private async Task<(IUnitOfWork, DataContext, OcrService, MangaFile)> Setup(bool enableOcr = true)
    {
        var (unitOfWork, context, _) = await CreateDatabase();
        _fileSystem.AddFile(PdfPath, new MockFileData("pdf") { LastWriteTime = WriteTime });

        var file = new MangaFileBuilder(PdfPath, MangaFormat.Pdf, 3).Build();
        var series = new SeriesBuilder("Quartet")
            .WithFormat(MangaFormat.Pdf)
            .WithVolume(new VolumeBuilder(Parser.LooseLeafVolume)
                .WithChapter(new ChapterBuilder("1").WithFile(file).Build())
                .Build())
            .Build();
        var library = new LibraryBuilder("Textbooks", LibraryType.Book).WithSeries(series).Build();
        library.EnableOcr = enableOcr;
        context.Library.Add(library);
        await context.SaveChangesAsync();

        var service = new OcrService(Substitute.For<ILogger<OcrService>>(), unitOfWork, _bookService,
            new DirectoryService(Substitute.For<ILogger<DirectoryService>>(), _fileSystem));
        return (unitOfWork, context, service, file);
    }

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
}
