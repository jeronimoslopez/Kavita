using System.Threading;
using System.Threading.Tasks;
using Kavita.Models.DTOs.Ocr;

namespace Kavita.API.Services;

public interface IOcrService
{
    /// <summary>
    /// Sends the series' PDFs that changed since their last check back to be checked for text, and queues the check
    /// </summary>
    /// <remarks>Only touches the database, so the scanner can call it for every series</remarks>
    Task MarkSeriesForCheckAsync(int seriesId, CancellationToken ct = default);
    /// <summary>
    /// Checks every unchecked PDF in OCR libraries for pages without text, and queues those pages for OCR
    /// </summary>
    /// <remarks>Takes no arguments so a queued run can be found and not queued twice</remarks>
    Task DetectNewFilesAsync();

    /// <summary>
    /// The file the worker should OCR next, or null when nothing is waiting
    /// </summary>
    Task<int?> GetNextFileIdAsync(CancellationToken ct = default);
    /// <summary>
    /// OCRs the file's next pending page with the sidecar and stores the lines
    /// </summary>
    Task<OcrPageResult> ProcessNextPageAsync(int mangaFileId, string sidecarUrl, CancellationToken ct = default);

    Task<OcrChapterStatusDto?> GetChapterStatusAsync(int chapterId, CancellationToken ct = default);
    /// <param name="pageNum">1-based, matching pdf.js</param>
    Task<OcrPageDto?> GetPageAsync(int chapterId, int pageNum, CancellationToken ct = default);
    /// <summary>
    /// Moves the chapter's PDF ahead of the queue, because someone opened it
    /// </summary>
    Task PrioritizeChapterAsync(int chapterId, CancellationToken ct = default);
    /// <summary>
    /// Queues the series' PDFs for OCR ahead of everything else
    /// </summary>
    /// <param name="replaceExistingText">OCR every page and replace any text the PDF already has</param>
    /// <returns>How many files were queued</returns>
    Task<int> RunSeriesAsync(int seriesId, bool replaceExistingText, CancellationToken ct = default);
    /// <summary>
    /// Deletes the series' OCR text and keeps rescans from queueing it again
    /// </summary>
    Task RemoveSeriesAsync(int seriesId, CancellationToken ct = default);
}

public enum OcrPageOutcome
{
    /// <summary>
    /// A page got its lines
    /// </summary>
    PageDone,
    /// <summary>
    /// A page couldn't be rendered or the sidecar refused it
    /// </summary>
    PageFailed,
    /// <summary>
    /// No pages are left, or the file is no longer queued
    /// </summary>
    FileFinished,
    /// <summary>
    /// The file changed on disk, so its OCR was dropped and it goes back to being checked
    /// </summary>
    FileChanged,
    /// <summary>
    /// The sidecar didn't answer. Nothing was changed
    /// </summary>
    SidecarUnavailable,
}

/// <param name="PageNum">1-based page the outcome is about, when there is one</param>
public sealed record OcrPageResult(OcrPageOutcome Outcome, int SeriesId = 0, int ChapterId = 0, int PageNum = 0);

/// <summary>
/// Wakes the OCR worker when there's new work, so it doesn't wait for its next poll
/// </summary>
public interface IOcrWorkerSignal
{
    void Wake();
}
