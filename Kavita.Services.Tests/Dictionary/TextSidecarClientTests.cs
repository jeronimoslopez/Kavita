using System.Text.Json;
using Flurl.Http.Testing;
using Kavita.API.Services;
using Kavita.Models.DTOs.Dictionary;
using Kavita.Services.Dictionary;

namespace Kavita.Services.Tests.Dictionary;

public class TextSidecarClientTests : IDisposable
{
    private static readonly string SampleResponse = File.ReadAllText(
        Path.Join(Directory.GetCurrentDirectory(), "../../../../text-sidecar-contract/sample-response.json"));

    private readonly HttpTest _httpTest = new();
    private readonly TextSidecarClient _client = new();

    public void Dispose() => _httpTest.Dispose();

    private static List<TokenizeBlockDto> SampleBlocks() =>
    [
        new("0", "今日は日本の経済政策について話した。"),
        new("1", "𠮷野さんは辛いカレーを食べた😀"),
        new("2", "上手に店を開く。"),
    ];

    [Fact]
    public async Task Tokenize_PostsTheBlocksAndReadsTheAnswer()
    {
        _httpTest.RespondWith(SampleResponse);

        var response = await _client.TokenizeAsync("http://text:8000/", SampleBlocks());

        var call = Assert.Single(_httpTest.CallLog);
        Assert.Equal("http://text:8000/tokenize", call.Request.Url.ToString());
        using var body = JsonDocument.Parse(call.RequestBody!);
        Assert.Equal(["0", "1", "2"], body.RootElement.GetProperty("blocks").EnumerateArray().Select(b => b.GetProperty("id").GetString()));
        Assert.Equal("上手に店を開く。", body.RootElement.GetProperty("blocks")[2].GetProperty("text").GetString());
        Assert.Equal(14, response.Words.Count);
        Assert.Equal(new TokenDto(6, 8, 8, "つらい", null), response.Blocks[1].Tokens[4]);
    }

    [Fact]
    public async Task Tokenize_EmptyAndOversizedBlocks_AreNotSentAndGetNoTokens()
    {
        _httpTest.RespondWith(SampleResponse);
        var blocks = SampleBlocks();
        blocks.Insert(1, new TokenizeBlockDto("9", string.Empty));
        blocks.Add(new TokenizeBlockDto("10", new string('あ', TextSidecarClient.MaxCodePointsPerBlock + 1)));

        var response = await _client.TokenizeAsync("http://text:8000", blocks);

        using var body = JsonDocument.Parse(Assert.Single(_httpTest.CallLog).RequestBody!);
        Assert.Equal(3, body.RootElement.GetProperty("blocks").GetArrayLength());
        Assert.Equal(["0", "9", "1", "2", "10"], response.Blocks.Select(b => b.Id));
        Assert.Empty(response.Blocks[1].Tokens);
        Assert.Empty(response.Blocks[4].Tokens);
        Assert.NotEmpty(response.Blocks[3].Tokens);
    }

    [Fact]
    public async Task Tokenize_OnlyEmptyBlocks_SendsNothing()
    {
        var response = await _client.TokenizeAsync("http://text:8000", [new TokenizeBlockDto("0", string.Empty)]);

        Assert.Empty(_httpTest.CallLog);
        Assert.Empty(Assert.Single(response.Blocks).Tokens);
    }

    [Fact]
    public async Task Tokenize_MoreBlocksThanOneRequestHolds_SplitsThem()
    {
        _httpTest.RespondWith(Answer(0, TextSidecarClient.MaxBlocksPerRequest))
            .RespondWith(Answer(TextSidecarClient.MaxBlocksPerRequest, 2));
        var blocks = Enumerable.Range(0, TextSidecarClient.MaxBlocksPerRequest + 2)
            .Select(i => new TokenizeBlockDto(i.ToString(), "猫"))
            .ToList();

        var response = await _client.TokenizeAsync("http://text:8000", blocks);

        Assert.Equal(2, _httpTest.CallLog.Count);
        Assert.Equal(2, response.Words.Count);
        Assert.Equal(0, response.Blocks[0].Tokens[0].W);
        Assert.Equal(1, response.Blocks[^1].Tokens[0].W);
    }

    /// <summary>
    /// One word, and a token on it for each of the blocks from <paramref name="first"/>
    /// </summary>
    private static string Answer(int first, int count)
    {
        return JsonSerializer.Serialize(new TokenizeResponseDto
        {
            Version = "lexicon=x;sidecar=1",
            Words = [new SidecarWordDto { Lemma = "猫", Pos = "名詞", Reading = "ねこ", Readings = ["ねこ"] }],
            Blocks = Enumerable.Range(first, count)
                .Select(i => new TokenizedBlockDto { Id = i.ToString(), Tokens = [new TokenDto(0, 1, 0, "ねこ", null)] })
                .ToList(),
        });
    }

    [Fact]
    public async Task Tokenize_Timeout_IsUnavailable()
    {
        _httpTest.SimulateTimeout();

        await Assert.ThrowsAsync<TextSidecarUnavailableException>(() => _client.TokenizeAsync("http://text:8000", SampleBlocks()));
    }

    [Fact]
    public async Task Tokenize_NoAnswer_IsUnavailable()
    {
        _httpTest.SimulateException(new HttpRequestException("Connection refused"));

        await Assert.ThrowsAsync<TextSidecarUnavailableException>(() => _client.TokenizeAsync("http://text:8000", SampleBlocks()));
    }

    [Fact]
    public async Task Tokenize_ErrorStatus_IsRejectedWithTheBody()
    {
        _httpTest.RespondWith("blocks too large", 413);

        var ex = await Assert.ThrowsAsync<TextSidecarRejectedException>(() => _client.TokenizeAsync("http://text:8000", SampleBlocks()));

        Assert.Equal(413, ex.StatusCode);
        Assert.Contains("blocks too large", ex.Message);
    }

    [Fact]
    public async Task Tokenize_AnswerMissingABlock_IsRejected()
    {
        _httpTest.RespondWith(Answer(0, 2));

        await Assert.ThrowsAsync<TextSidecarRejectedException>(() => _client.TokenizeAsync("http://text:8000", SampleBlocks()));
    }
}
