using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kavita.Models.DTOs.Ocr;
using Kavita.Models.Entities;
using Kavita.Models.Entities.Enums;

namespace Kavita.API.Repositories;

public interface IOcrRepository
{
    void Add(OcrPage page);
    /// <summary>
    /// PDFs in the series. By default only those whose library has OCR turned on
    /// </summary>
    Task<IList<MangaFile>> GetOcrFilesForSeriesAsync(int seriesId, bool includeLibrariesWithoutOcr = false, CancellationToken ct = default);
    /// <summary>
    /// Ids of PDFs in OCR libraries that haven't been checked for text since they last changed
    /// </summary>
    Task<IList<int>> GetFileIdsNeedingCheckAsync(CancellationToken ct = default);
    Task<MangaFile?> GetOcrFileAsync(int mangaFileId, CancellationToken ct = default);
    /// <summary>
    /// The chapter's PDF, whether or not its library has OCR turned on, so text already stored stays readable
    /// </summary>
    Task<MangaFile?> GetPdfFileForChapterAsync(int chapterId, CancellationToken ct = default);
    Task DeletePagesForFileAsync(int mangaFileId, CancellationToken ct = default);
    /// <summary>
    /// The file to OCR next: highest priority first, then the one queued longest ago
    /// </summary>
    Task<int?> GetNextFileIdToOcrAsync(CancellationToken ct = default);
    Task<OcrFileInfoDto?> GetOcrFileInfoAsync(int mangaFileId, CancellationToken ct = default);
    Task<OcrPage?> GetNextPendingPageAsync(int mangaFileId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<OcrPageState, int>> GetPageStateCountsAsync(int mangaFileId, CancellationToken ct = default);
    /// <param name="pageNumber">0-based</param>
    Task<OcrPage?> GetDonePageAsync(int mangaFileId, int pageNumber, CancellationToken ct = default);
    /// <returns>0-based page numbers</returns>
    Task<IList<int>> GetDonePageNumbersAsync(int mangaFileId, CancellationToken ct = default);
}
