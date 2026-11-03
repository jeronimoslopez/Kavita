using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kavita.API.Repositories;
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

    public async Task<IList<MangaFile>> GetOcrFilesForSeriesAsync(int seriesId, CancellationToken ct = default)
    {
        return await OcrFiles()
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

    public async Task DeletePagesForFileAsync(int mangaFileId, CancellationToken ct = default)
    {
        await context.OcrPage
            .Where(p => p.MangaFileId == mangaFileId)
            .ExecuteDeleteAsync(ct);
    }

    private IQueryable<MangaFile> OcrFiles()
    {
        return context.MangaFile
            .Where(f => f.Format == MangaFormat.Pdf && f.Chapter.Volume.Series.Library.EnableOcr);
    }
}
