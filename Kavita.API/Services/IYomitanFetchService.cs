using System.Threading;
using System.Threading.Tasks;
using Kavita.Models.DTOs.Yomitan;

namespace Kavita.API.Services;

/// <summary>
/// Proxy for Yomitan audio downloads whose servers block cross-origin fetches from the reader
/// </summary>
public interface IYomitanFetchService
{
    /// <summary>
    /// True when the url is https on the default port and none of its host's addresses is internal (loopback, private,
    /// link-local, CGNAT, unique-local, unspecified or multicast)
    /// </summary>
    Task<bool> IsAllowedAsync(string url, CancellationToken ct = default);
    /// <summary>
    /// Fetches the url, re-checking every redirect hop. Non-2xx statuses are returned, not thrown.
    /// </summary>
    /// <exception cref="Kavita.Common.KavitaException">When the url, method or a redirect is not allowed, or the body is too large</exception>
    Task<YomitanFetchResponseDto> FetchAsync(YomitanFetchRequestDto request, CancellationToken ct = default);
}
