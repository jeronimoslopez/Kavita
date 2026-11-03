using System;
using System.Threading;
using System.Threading.Tasks;
using Kavita.Models.DTOs.Ocr;

namespace Kavita.API.Services;

public interface IOcrSidecarClient
{
    /// <exception cref="OcrSidecarUnavailableException">The sidecar didn't answer</exception>
    /// <exception cref="OcrSidecarRejectedException">The sidecar answered with an error for this image</exception>
    Task<OcrSidecarResponseDto> RecognizeAsync(string sidecarUrl, byte[] image, CancellationToken ct = default);
}

/// <summary>
/// No answer at all: the container is down, unreachable or timed out. Says nothing about the page itself
/// </summary>
public class OcrSidecarUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The sidecar answered, and refused or failed on this particular image
/// </summary>
public class OcrSidecarRejectedException(int statusCode, string message, Exception? inner = null) : Exception(message, inner)
{
    public int StatusCode { get; } = statusCode;
}
