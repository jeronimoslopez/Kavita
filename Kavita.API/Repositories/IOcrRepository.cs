using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kavita.Models.Entities;

namespace Kavita.API.Repositories;

public interface IOcrRepository
{
    void Add(OcrPage page);
    /// <summary>
    /// PDFs in the series whose library has OCR turned on
    /// </summary>
    Task<IList<MangaFile>> GetOcrFilesForSeriesAsync(int seriesId, CancellationToken ct = default);
    /// <summary>
    /// Ids of PDFs in OCR libraries that haven't been checked for text since they last changed
    /// </summary>
    Task<IList<int>> GetFileIdsNeedingCheckAsync(CancellationToken ct = default);
    Task<MangaFile?> GetOcrFileAsync(int mangaFileId, CancellationToken ct = default);
    Task DeletePagesForFileAsync(int mangaFileId, CancellationToken ct = default);
}
