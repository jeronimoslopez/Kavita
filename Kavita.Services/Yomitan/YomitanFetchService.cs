using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kavita.API.Services;
using Kavita.Common;
using Kavita.Models.DTOs.Yomitan;
using Microsoft.Extensions.Logging;

namespace Kavita.Services.Yomitan;

/// <summary>
/// Proxy for the audio Yomitan downloads (Jisho, Wiktionary, Lingua Libre, JapanesePod101 and the CDNs behind them).
/// Any public https host is allowed. Hosts that resolve to an internal address are refused, both before each request and
/// again inside the socket connect so DNS rebinding between the check and the connect can't reach the internal network.
/// </summary>
public class YomitanFetchService(ILogger<YomitanFetchService> logger) : IYomitanFetchService
{
    public const long MaxResponseBytes = 10 * 1024 * 1024;
    private const int MaxRedirects = 5;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private static readonly string[] ForwardedHeaders = ["Content-Type", "Accept", "Accept-Language"];

    // No cookies and no automatic redirects. Each request is built fresh, so the reader's auth never goes upstream.
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = ConnectToPublicAddressAsync,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
        // Wikimedia rejects requests that carry no User-Agent
        DefaultRequestHeaders = { { "User-Agent", "Kavita-Yomitan/1.0 (+https://www.kavitareader.com)" } },
    };

    public async Task<bool> IsAllowedAsync(string url, CancellationToken ct = default)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && await IsAllowedAsync(uri, ct);
    }

    private static async Task<bool> IsAllowedAsync(Uri uri, CancellationToken ct)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        try
        {
            var addresses = await ResolveAsync(uri.IdnHost, ct);
            return addresses.Length > 0 && !addresses.Any(IsBlockedAddress);
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public async Task<YomitanFetchResponseDto> FetchAsync(YomitanFetchRequestDto request, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || !await IsAllowedAsync(uri, ct))
        {
            throw new KavitaException("Url is not allowed");
        }

        var method = string.IsNullOrEmpty(request.Method) ? "GET" : request.Method.ToUpperInvariant();
        if (method is not ("GET" or "POST"))
        {
            throw new KavitaException("Only GET and POST are allowed");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        var token = timeout.Token;

        var body = request.Body;
        var redirects = 0;
        try
        {
            while (true)
            {
                using var message = CreateMessage(uri, method, body, request);
                using var response = await Client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);

                var status = (int) response.StatusCode;
                if (status is (301 or 302 or 303 or 307 or 308) && response.Headers.Location != null)
                {
                    if (++redirects > MaxRedirects) throw new KavitaException("Too many redirects");
                    var next = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(uri, response.Headers.Location);
                    if (!await IsAllowedAsync(next, token))
                    {
                        logger.LogWarning("[Yomitan] Blocked redirect from {From} to {To}", uri, next);
                        throw new KavitaException("Redirect to a url that is not allowed");
                    }

                    // Same rewrite the fetch spec applies: 303, and 301/302 after a POST, become a bodiless GET
                    if (status == 303 || (status is (301 or 302) && method == "POST"))
                    {
                        method = "GET";
                        body = null;
                    }
                    uri = next;
                    continue;
                }

                if (response.Content.Headers.ContentLength > MaxResponseBytes)
                {
                    throw new KavitaException("Response is too large");
                }

                var bytes = await ReadCappedAsync(response.Content, token);
                return new YomitanFetchResponseDto
                {
                    Status = status,
                    Url = uri.AbsoluteUri,
                    ContentType = response.Content.Headers.ContentType?.ToString(),
                    Body = Convert.ToBase64String(bytes),
                };
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new KavitaException("Upstream request timed out");
        }
        catch (HttpRequestException ex) when (ex.InnerException is BlockedAddressException)
        {
            logger.LogWarning(ex, "[Yomitan] Blocked connection to {Url}: {Reason}", uri, ex.InnerException.Message);
            throw new KavitaException("Url is not allowed");
        }
        catch (HttpRequestException ex)
        {
            logger.LogDebug(ex, "[Yomitan] Fetch of {Url} failed", uri);
            throw new KavitaException("Upstream request failed");
        }
    }

    /// <summary>
    /// Resolves the host and connects only when every address is public. The connect uses the addresses that were
    /// checked, so a second DNS answer can't redirect it.
    /// </summary>
    private static async ValueTask<Stream> ConnectToPublicAddressAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = await ResolveAsync(host, ct);
        if (addresses.Length == 0) throw new BlockedAddressException($"{host} did not resolve");
        var blocked = addresses.FirstOrDefault(IsBlockedAddress);
        if (blocked != null) throw new BlockedAddressException($"{host} resolves to internal address {blocked}");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        var trimmed = host.Trim('[', ']');
        if (IPAddress.TryParse(trimmed, out var literal)) return [literal];
        return await Dns.GetHostAddressesAsync(trimmed, ct);
    }

    /// <summary>
    /// Loopback, private, link-local, CGNAT, unique-local, unspecified, multicast and reserved ranges
    /// </summary>
    public static bool IsBlockedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 0                                   // 0.0.0.0/8 unspecified
                   || b[0] == 10                               // 10/8
                   || b[0] == 127                              // loopback
                   || (b[0] == 100 && (b[1] & 0xC0) == 64)     // 100.64/10 CGNAT
                   || (b[0] == 169 && b[1] == 254)             // link-local
                   || (b[0] == 172 && (b[1] & 0xF0) == 16)     // 172.16/12
                   || (b[0] == 192 && b[1] == 168)             // 192.168/16
                   || b[0] >= 224;                             // multicast, reserved and broadcast
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None) || IPAddress.IsLoopback(address)) return true;
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return true;
            var b = address.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true; // fc00::/7 unique-local

            // NAT64 (64:ff9b::/96) carries an IPv4 address in its last four bytes
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b.Skip(4).Take(8).All(x => x == 0))
            {
                return IsBlockedAddress(new IPAddress(b[12..]));
            }
            return false;
        }

        return true;
    }

    private static HttpRequestMessage CreateMessage(Uri uri, string method, string? body, YomitanFetchRequestDto request)
    {
        var message = new HttpRequestMessage(method == "POST" ? HttpMethod.Post : HttpMethod.Get, uri);
        string? contentType = null;
        if (request.Headers != null)
        {
            foreach (var (name, value) in request.Headers)
            {
                var allowed = ForwardedHeaders.FirstOrDefault(h => h.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (allowed == null) continue;
                if (allowed == "Content-Type")
                {
                    contentType = value;
                    continue;
                }
                message.Headers.TryAddWithoutValidation(allowed, value);
            }
        }

        if (body != null && method == "POST")
        {
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            if (contentType != null && MediaTypeHeaderValue.TryParse(contentType, out var parsed))
            {
                content.Headers.ContentType = parsed;
            }
            message.Content = content;
        }
        return message;
    }

    private static async Task<byte[]> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (memory.Length + read > MaxResponseBytes)
            {
                throw new KavitaException("Response is too large");
            }
            await memory.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return memory.ToArray();
    }

    private sealed class BlockedAddressException(string message) : IOException(message);
}
