using System.Net;
using Kavita.Common;
using Kavita.Models.DTOs.Yomitan;
using Kavita.Services.Yomitan;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kavita.Services.Tests.Yomitan;

public class YomitanFetchServiceTests
{
    private readonly YomitanFetchService _service = new(NullLogger<YomitanFetchService>.Instance);

    [Theory]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://localhost/")]
    [InlineData("http://example.com/")]
    [InlineData("https://example.com:8443/")]
    [InlineData("https://user:pass@example.com/")]
    [InlineData("https://[::1]/")]
    [InlineData("https://10.0.0.1/")]
    [InlineData("ftp://example.com/")]
    [InlineData("not a url")]
    public async Task RejectsInternalOrNonHttpsUrls(string url)
    {
        Assert.False(await _service.IsAllowedAsync(url));
        await Assert.ThrowsAsync<KavitaException>(() => _service.FetchAsync(new YomitanFetchRequestDto { Url = url }));
    }

    [Fact]
    public async Task RejectsUnsupportedMethod()
    {
        await Assert.ThrowsAsync<KavitaException>(() => _service.FetchAsync(new YomitanFetchRequestDto
        {
            Url = "https://93.184.215.14/",
            Method = "DELETE",
        }));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.1.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("0.0.0.0", true)]
    [InlineData("224.0.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::1", true)]
    [InlineData("::", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("ff02::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("::ffff:8.8.8.8", false)]
    [InlineData("64:ff9b::a00:1", true)]
    [InlineData("2606:4700::1111", false)]
    public void IsBlockedAddress(string address, bool blocked)
    {
        Assert.Equal(blocked, YomitanFetchService.IsBlockedAddress(IPAddress.Parse(address)));
    }
}
