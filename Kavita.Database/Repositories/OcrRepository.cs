using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kavita.API.Repositories;
using Kavita.Models.DTOs.Ocr;
using Kavita.Models.Entities;
using Kavita.Models.Entities.Enums;
using Microsoft.EntityFrameworkCore;

namespace Kavita.Database.Repositories;

public class OcrRepository(DataContext context) : IOcrRepository
{
    public void Add(OcrPage page)
    {
        context.OcrPage.Add(page);
    }

    public async Task<IList<MangaFile>> GetOcrFilesForSeriesAsync(int seriesId, bool includeLibrariesWithoutOcr = false,
        CancellationToken ct = default)
    {
        var files = includeLibrariesWithoutOcr ? PdfFiles() : OcrFiles();
        return await files
            .Where(f => f.Chapter.Volume.SeriesId == seriesId)
            .ToListAsync(ct);
    }

    public async Task<IList<int>> GetFileIdsNeedingCheckAsync(CancellationToken ct = default)
    {
        return await OcrFiles()
            .Where(f => f.OcrStatus == OcrStatus.NotChecked)
            .Select(f => f.Id)
            .ToListAsync(ct);
    }

    public async Task<MangaFile?> GetOcrFileAsync(int mangaFileId, CancellationToken ct = default)
    {
        return await OcrFiles().FirstOrDefaultAsync(f => f.Id == mangaFileId, ct);
    }

    public async Task<MangaFile?> GetPdfFileForChapterAsync(int chapterId, CancellationToken ct = default)
    {
        return await PdfFiles()
            .Where(f => f.ChapterId == chapterId)
            .OrderBy(f => f.Id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task DeletePagesForFileAsync(int mangaFileId, CancellationToken ct = default)
    {
        await context.OcrPage
            .Where(p => p.MangaFileId == mangaFileId)
            .ExecuteDeleteAsync(ct);
    }

    public async Task<int?> GetNextFileIdToOcrAsync(CancellationToken ct = default)
    {
        return await OcrFiles()
            .Where(f => f.OcrStatus == OcrStatus.Pending || f.OcrStatus == OcrStatus.Processing)
            .OrderByDescending(f => f.OcrPriority)
            .ThenBy(f => f.OcrRequestedUtc)
            .ThenBy(f => f.Id)
            .Select(f => (int?) f.Id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<OcrFileInfoDto?> GetOcrFileInfoAsync(int mangaFileId, CancellationToken ct = default)
    {
        return await OcrFiles()
            .Where(f => f.Id == mangaFileId)
            .Select(f => new OcrFileInfoDto
            {
                MangaFileId = f.Id,
                FilePath = f.FilePath,
                ChapterId = f.ChapterId,
                SeriesId = f.Chapter.Volume.SeriesId,
                LibraryId = f.Chapter.Volume.Series.LibraryId,
                SeriesName = f.Chapter.Volume.Series.Name,
                OcrStatus = f.OcrStatus,
                OcrSourceWriteTimeUtc = f.OcrSourceWriteTimeUtc,
            })
            .FirstOrDefaultAsync(ct);
    }

    public async Task<OcrPage?> GetNextPendingPageAsync(int mangaFileId, CancellationToken ct = default)
    {
        return await context.OcrPage
            .Where(p => p.MangaFileId == mangaFileId && p.State == OcrPageState.Pending)
            .OrderBy(p => p.PageNumber)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyDictionary<OcrPageState, int>> GetPageStateCountsAsync(int mangaFileId, CancellationToken ct = default)
    {
        return await context.OcrPage
            .Where(p => p.MangaFileId == mangaFileId)
            .GroupBy(p => p.State)
            .ToDictionaryAsync(g => g.Key, g => g.Count(), ct);
    }

    public async Task<OcrPage?> GetDonePageAsync(int mangaFileId, int pageNumber, CancellationToken ct = default)
    {
        return await context.OcrPage
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.MangaFileId == mangaFileId && p.PageNumber == pageNumber && p.State == OcrPageState.Done, ct);
    }

    public async Task<IList<int>> GetDonePageNumbersAsync(int mangaFileId, CancellationToken ct = default)
    {
        return await context.OcrPage
            .Where(p => p.MangaFileId == mangaFileId && p.State == OcrPageState.Done)
            .OrderBy(p => p.PageNumber)
            .Select(p => p.PageNumber)
            .ToListAsync(ct);
    }

    private IQueryable<MangaFile> PdfFiles()
    {
        return context.MangaFile.Where(f => f.Format == MangaFormat.Pdf);
    }

    private IQueryable<MangaFile> OcrFiles()
    {
        return PdfFiles().Where(f => f.Chapter.Volume.Series.Library.EnableOcr);
    }
}
