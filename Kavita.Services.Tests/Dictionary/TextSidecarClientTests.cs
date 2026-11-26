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
        Assert.Equal(28, response.Words.Count);
        Assert.Equal(new TokenDto(6, 8, 8, "つらい", null), response.Blocks[1].Tokens[4]);
        Assert.Equal([new PhraseDto(3, 7, 14)], response.Blocks[2].Phrases);
        Assert.Equal([6], response.Blocks[1].Blind);
        Assert.Equal(3, response.Words[24].Entries!.Count);
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

    [Fact]
    public async Task Tokenize_SplitAnswers_KeepEachBlocksBlindOffsetsAndEachWordsEntriesAndRestrictions()
    {
        var first = new TokenizeResponseDto
        {
            Version = "lexicon=x;sidecar=3",
            Words = [new SidecarWordDto
            {
                Lemma = "今日", Pos = "名詞", Reading = "きょう", Readings = ["きょう", "こんにち"],
                Senses = [new SenseDto("1:1", ["n"], ["today"]), new SenseDto("1:2", ["n"], ["these days"], ["こんにち"])],
                Entries = [new WordEntryDto(1, true, ["きょう", "こんにち"])],
            }],
            Blocks = Enumerable.Range(0, TextSidecarClient.MaxBlocksPerRequest)
                .Select(i => new TokenizedBlockDto { Id = i.ToString(), Tokens = [new TokenDto(0, 2, 0, "きょう", null)], Blind = i == 0 ? [0] : [] })
                .ToList(),
        };
        var second = new TokenizeResponseDto
        {
            Version = "lexicon=x;sidecar=3",
            Words = [new SidecarWordDto
            {
                Lemma = "瞬く", Pos = "動詞", Reading = "しばたたく", Readings = ["しばたたく", "またたく"],
                Senses = [new SenseDto("2:1", ["v5k"], ["to blink"]), new SenseDto("3:1", ["v5k"], ["to twinkle"])],
                Entries = [new WordEntryDto(2, false, ["しばたたく"]), new WordEntryDto(3, true, ["またたく"])],
            }],
            Blocks = [new TokenizedBlockDto { Id = TextSidecarClient.MaxBlocksPerRequest.ToString(), Tokens = [new TokenDto(0, 2, 0, "しばたたく", null)], Blind = [0] }],
        };
        _httpTest.RespondWithJson(first).RespondWithJson(second);
        var blocks = Enumerable.Range(0, TextSidecarClient.MaxBlocksPerRequest + 1)
            .Select(i => new TokenizeBlockDto(i.ToString(), "今日"))
            .ToList();

        var response = await _client.TokenizeAsync("http://text:8000", blocks);

        Assert.Equal(2, _httpTest.CallLog.Count);
        Assert.Equal([0], response.Blocks[0].Blind);
        Assert.Empty(response.Blocks[1].Blind);
        Assert.Equal([0], response.Blocks[^1].Blind);
        Assert.Equal(1, response.Blocks[^1].Tokens[0].W);
        Assert.Equal([1L], response.Words[0].Entries!.Select(e => e.Seq));
        Assert.Equal(["こんにち"], response.Words[0].Senses[1].R!);
        Assert.Null(response.Words[0].Senses[0].R);
        Assert.Equal([2L, 3L], response.Words[1].Entries!.Select(e => e.Seq));
    }

    [Fact]
    public void Answer_FromVersion2_ReadsWithNoEntriesRestrictionsOrBlindOffsets()
    {
        var response = JsonSerializer.Deserialize<TokenizeResponseDto>("""
            {"version": "lexicon=x;sidecar=2",
             "words": [{"lemma": "猫", "pos": "名詞", "isProper": false, "reading": "ねこ", "jlpt": null, "readings": ["ねこ"],
                        "senses": [{"key": "1:1", "pos": ["n"], "gloss": ["cat"]}]}],
             "blocks": [{"id": "0", "tokens": [[0, 1, 0, "ねこ", null]], "phrases": []}]}
            """)!;

        Assert.Null(response.Words[0].Entries);
        Assert.Null(response.Words[0].Senses[0].R);
        Assert.Empty(response.Blocks[0].Blind);
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

    [Theory]
    [InlineData(413)]
    [InlineData(422)]
    public async Task Tokenize_StatusAboutTheText_IsRejectedWithTheBody(int status)
    {
        _httpTest.RespondWith("blocks too large", status);

        var ex = await Assert.ThrowsAsync<TextSidecarRejectedException>(() => _client.TokenizeAsync("http://text:8000", SampleBlocks()));

        Assert.Equal(status, ex.StatusCode);
        Assert.Contains("blocks too large", ex.Message);
    }

    [Theory]
    [InlineData(404)] // a mistyped URL
    [InlineData(500)]
    [InlineData(502)] // a proxy in front of a stopped sidecar
    [InlineData(503)]
    public async Task Tokenize_StatusAboutTheSidecar_IsUnavailable(int status)
    {
        _httpTest.RespondWith("Bad Gateway", status);

        await Assert.ThrowsAsync<TextSidecarUnavailableException>(() => _client.TokenizeAsync("http://text:8000", SampleBlocks()));
    }

    [Fact]
    public async Task Tokenize_AnswerMissingABlock_IsRejected()
    {
        _httpTest.RespondWith(Answer(0, 2));

        await Assert.ThrowsAsync<TextSidecarRejectedException>(() => _client.TokenizeAsync("http://text:8000", SampleBlocks()));
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task GetVersion_ReadsHealthAndKeepsTheAnswerForTenMinutes()
    {
        var time = new ManualTime();
        var client = new TextSidecarClient(time);
        _httpTest.RespondWithJson(new { version = "a;sidecar=1" }).RespondWithJson(new { version = "a;sidecar=2" });

        Assert.Equal("a;sidecar=1", await client.GetVersionAsync("http://text:8000/"));
        time.Now += TextSidecarVersionTtl.Known - TimeSpan.FromSeconds(1);
        Assert.Equal("a;sidecar=1", await client.GetVersionAsync("http://text:8000"));
        time.Now += TimeSpan.FromSeconds(1);
        Assert.Equal("a;sidecar=2", await client.GetVersionAsync("http://text:8000"));

        Assert.Equal(2, _httpTest.CallLog.Count);
        Assert.Equal("http://text:8000/health", _httpTest.CallLog[0].Request.Url.ToString());
    }

    [Theory]
    [InlineData(503, "{\"version\": \"a\"}")]
    [InlineData(200, "{}")]
    [InlineData(200, "{\"version\": \"\"}")]
    [InlineData(200, "not json")]
    public async Task GetVersion_NoUsableAnswer_IsNullAndAskedAgainAfterAMinute(int status, string body)
    {
        var time = new ManualTime();
        var client = new TextSidecarClient(time);
        _httpTest.RespondWith(body, status).RespondWithJson(new { version = "a;sidecar=2" });

        Assert.Null(await client.GetVersionAsync("http://text:8000"));
        Assert.Null(await client.GetVersionAsync("http://text:8000"));
        time.Now += TextSidecarVersionTtl.Unknown;
        Assert.Equal("a;sidecar=2", await client.GetVersionAsync("http://text:8000"));
        Assert.Equal(2, _httpTest.CallLog.Count);
    }

    [Fact]
    public async Task GetVersion_Timeout_IsNull()
    {
        _httpTest.SimulateTimeout();

        Assert.Null(await _client.GetVersionAsync("http://text:8000"));
    }
}
