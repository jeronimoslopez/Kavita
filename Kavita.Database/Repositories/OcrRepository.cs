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
        var files = includeLibrariesWithoutOcr ? OcrSourceFiles() : OcrFiles();
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

    public async Task<MangaFile?> GetOcrSourceFileForChapterAsync(int chapterId, CancellationToken ct = default)
    {
        return await OcrSourceFiles()
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
                Format = f.Format,
                LibraryType = f.Chapter.Volume.Series.Library.Type,
                OcrEngine = f.Chapter.Volume.Series.Library.OcrEngine,
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

    public async Task<IList<OcrPage>> GetDonePagesAsync(int mangaFileId, CancellationToken ct = default)
    {
        return await context.OcrPage
            .AsNoTracking()
            .Where(p => p.MangaFileId == mangaFileId && p.State == OcrPageState.Done)
            .OrderBy(p => p.PageNumber)
            .ToListAsync(ct);
    }

    public async Task<IList<int>> GetDonePageNumbersAsync(int mangaFileId, CancellationToken ct = default)
    {
        return await context.OcrPage
            .Where(p => p.MangaFileId == mangaFileId && p.State == OcrPageState.Done)
            .OrderBy(p => p.PageNumber)
            .Select(p => p.PageNumber)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Files OCR can read. An archive only counts when it's its chapter's only file, because its page numbers are then
    /// the reader's page numbers
    /// </summary>
    private IQueryable<MangaFile> OcrSourceFiles()
    {
        return context.MangaFile.Where(f => f.Format == MangaFormat.Pdf ||
                                            (f.Format == MangaFormat.Archive && f.Chapter.Files.Count == 1));
    }

    private IQueryable<MangaFile> OcrFiles()
    {
        return OcrSourceFiles().Where(f => f.Chapter.Volume.Series.Library.EnableOcr);
    }
}
