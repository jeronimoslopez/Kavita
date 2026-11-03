using System.Threading;
using System.Threading.Tasks;

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
}
