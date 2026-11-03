using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Flurl.Http;
using Kavita.API.Services;
using Kavita.Models.DTOs.Ocr;

namespace Kavita.Services.Ocr;

public class OcrSidecarClient : IOcrSidecarClient
{
    /// <summary>
    /// The sidecar handles one page at a time, so a request can wait behind another page before its own 2–5 s of work
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    public async Task<OcrSidecarResponseDto> RecognizeAsync(string sidecarUrl, byte[] image, CancellationToken ct = default)
    {
        try
        {
            return await $"{sidecarUrl.TrimEnd('/')}/ocr"
                .WithTimeout(Timeout)
                .PostMultipartAsync(content => content.AddFile("image", new MemoryStream(image), "page.png", "image/png"),
                    cancellationToken: ct)
                .ReceiveJson<OcrSidecarResponseDto>();
        }
        catch (FlurlHttpException) when (ct.IsCancellationRequested)
        {
            // Kavita is shutting down. The page is untouched and the sidecar is fine
            throw new OperationCanceledException(ct);
        }
        catch (FlurlHttpTimeoutException ex)
        {
            throw new OcrSidecarUnavailableException($"The OCR sidecar at {sidecarUrl} timed out", ex);
        }
        catch (FlurlHttpException ex) when (ex.StatusCode == null)
        {
            throw new OcrSidecarUnavailableException($"The OCR sidecar at {sidecarUrl} can't be reached", ex);
        }
        catch (FlurlHttpException ex)
        {
            var body = await ex.GetResponseStringAsync();
            throw new OcrSidecarRejectedException(ex.StatusCode!.Value, $"The OCR sidecar answered {ex.StatusCode}: {body}", ex);
        }
    }
}
