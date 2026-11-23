using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kavita.Models.DTOs.Dictionary;

namespace Kavita.API.Services;

public interface ITextSidecarClient
{
    /// <summary>
    /// Posts the blocks to the sidecar's <c>/tokenize</c>. Each block is tokenized on its own, so no token crosses a block
    /// </summary>
    /// <param name="sidecarUrl">Base URL from <c>ServerSettingKey.TextSidecarUrl</c>, with or without a trailing slash</param>
    /// <param name="blocks">Sent in this order. Kavita uses the block's index as its id</param>
    /// <returns>The response with one block per block sent, in the same order</returns>
    /// <exception cref="TextSidecarUnavailableException">The sidecar didn't answer</exception>
    /// <exception cref="TextSidecarRejectedException">The sidecar answered with an error for these blocks</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled</exception>
    Task<TokenizeResponseDto> TokenizeAsync(string sidecarUrl, IReadOnlyList<TokenizeBlockDto> blocks, CancellationToken ct = default);
}

/// <summary>
/// The sidecar gave no answer, because it's down, unreachable or timed out. Says nothing about the text itself
/// </summary>
public class TextSidecarUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The sidecar answered, and refused or failed on this particular text
/// </summary>
public class TextSidecarRejectedException(int statusCode, string message, Exception? inner = null) : Exception(message, inner)
{
    public int StatusCode { get; } = statusCode;
}
