using System.Text.Json;
using Kavita.Models.DTOs.Dictionary;

namespace Kavita.Services.Tests.Dictionary;

public class TokenizeContractTests
{
    /// <summary>
    /// What the real sidecar returns for sample-request.json, copied from the shimen repo
    /// </summary>
    private static readonly string SampleResponse = File.ReadAllText(
        Path.Join(Directory.GetCurrentDirectory(), "../../../../text-sidecar-contract/sample-response.json"));

    [Fact]
    public void SampleResponse_Deserializes()
    {
        var response = JsonSerializer.Deserialize<TokenizeResponseDto>(SampleResponse);

        Assert.NotNull(response);
        Assert.StartsWith("sudachipy=", response.Version);
        Assert.EndsWith(";sidecar=3", response.Version);
        Assert.Equal(["0", "1", "2", "3", "4"], response.Blocks.Select(b => b.Id));

        var harsh = response.Words[8];
        Assert.Equal("辛い", harsh.Lemma);
        Assert.Equal("つらい", harsh.Reading);
        Assert.Equal(["つらい", "からい", "づらい", "ずらい"], harsh.Readings);
        Assert.Equal("1365860:1", harsh.Senses[0].Key);
        Assert.Contains(harsh.Senses, s => s.Key == "1365850:1");
        Assert.True(harsh.IsAmbiguous);
        Assert.False(response.Words[3].IsAmbiguous);
        Assert.Null(response.Words[4].Jlpt);
        Assert.True(response.Words[4].IsProper);

        var second = response.Blocks[1].Tokens;
        Assert.Equal(new TokenDto(0, 2, null, "𠮷", null), second[0]);
        Assert.Equal(new TokenDto(6, 8, 8, "つらい", null), second[4]);
        Assert.Equal(new TokenDto(15, 17, null, null, null), second[9]);

        var compound = response.Blocks[0].Tokens[4];
        Assert.Equal((6, 10, 4L, "けいざいせいさく"), (compound.S, compound.E, compound.W, compound.R));
        Assert.NotNull(compound.Sub);
        Assert.Equal([new SubTokenDto(6, 8, 2, "けいざい"), new SubTokenDto(8, 10, 3, "せいさく")], compound.Sub);

        // 神 and 様 come back joined as 神様, with the two as its parts
        var joined = response.Blocks[3].Tokens[0];
        Assert.Equal(new TokenDto(0, 2, 16, "かみさま", joined.Sub), joined);
        Assert.Equal([new SubTokenDto(0, 1, 15, "かみ"), new SubTokenDto(1, 2, null, "さま")], joined.Sub);
        Assert.Equal("神様", response.Words[16].Lemma);

        // 気がする spans 気, が and し, and no token points at its word
        Assert.Equal([new PhraseDto(10, 13, 22)], response.Blocks[3].Phrases);
        Assert.Equal(("気がする", "表現"), (response.Words[22].Lemma, response.Words[22].Pos));
        Assert.DoesNotContain(response.Blocks.SelectMany(b => b.Tokens), t => t.W == 22);
        Assert.Equal([new PhraseDto(3, 7, 14)], response.Blocks[2].Phrases);
        Assert.Equal("店を開く", response.Words[14].Lemma);
        Assert.Empty(response.Blocks[0].Phrases);
    }

    [Fact]
    public void SampleResponse_CarriesEntriesRestrictionsAndBlindOffsets()
    {
        var response = JsonSerializer.Deserialize<TokenizeResponseDto>(SampleResponse)!;

        // 瞬く has three entries, and the tokenizer read it しばたたく by fixed cost alone
        var blink = response.Words[24];
        Assert.Equal("瞬く", blink.Lemma);
        var entries = blink.Entries!;
        Assert.Equal([2831002L, 1341200L, 2831001L], entries.Select(e => e.Seq));
        Assert.Equal(["またたく", "まばたく", "まだたく"], entries[1].Readings);
        Assert.True(entries[1].Common);
        Assert.False(entries[0].Common);
        Assert.Equal(new TokenDto(2, 4, 24, "しばたたく", null), response.Blocks[4].Tokens[2]);
        Assert.Equal([2], response.Blocks[4].Blind);

        // 今日's second sense is restricted to こんにち, and its first takes every reading
        var today = response.Words[0];
        Assert.Null(today.Senses[0].R);
        Assert.Equal(["こんにち"], today.Senses[1].R!);
        Assert.Equal(1579110L, today.Senses[1].Seq);

        // 言う has one entry for both its readings, and 言っ reads いっ
        var say = response.Words[27];
        Assert.Equal(("言う", 1), (say.Lemma, say.Entries!.Count));
        Assert.Equal(["いう", "ゆう"], say.Entries[0].Readings);
        Assert.Equal(new TokenDto(17, 19, 27, "いっ", null), response.Blocks[4].Tokens[15]);

        Assert.All(response.Words, w => Assert.NotEmpty(w.Entries!));
        Assert.Equal([3], response.Blocks[0].Blind);
    }

    [Fact]
    public void Token_RoundTripsAsAnArray()
    {
        var token = new TokenDto(0, 5, 3, "kokuritsu", [new SubTokenDto(0, 2, 4, null), new SubTokenDto(2, 5, null, "toshokan")]);

        var json = JsonSerializer.Serialize(token);

        Assert.Equal("""[0,5,3,"kokuritsu",[[0,2,4,null],[2,5,null,"toshokan"]]]""", json);
        Assert.Equal(token.Sub, JsonSerializer.Deserialize<TokenDto>(json)!.Sub);
        Assert.Equal("[1,2,null,null,null]", JsonSerializer.Serialize(new TokenDto(1, 2, null, null, null)));
    }

    [Theory]
    [InlineData("[0,2,0,\"x\"]")]
    [InlineData("[0,2,0,\"x\",null,1]")]
    [InlineData("{\"s\":0}")]
    [InlineData("[0,2,0,\"x\",[[0,1,0]]]")]
    public void Token_RejectsTheWrongShape(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TokenDto>(json));
    }

    [Fact]
    public void Phrase_RoundTripsAsAnArray()
    {
        var json = JsonSerializer.Serialize(new PhraseDto(3, 7, 12));

        Assert.Equal("[3,7,12]", json);
        Assert.Equal(new PhraseDto(3, 7, 12), JsonSerializer.Deserialize<PhraseDto>(json));
    }

    [Theory]
    [InlineData("[0,2]")]
    [InlineData("[0,2,1,null]")]
    [InlineData("[0,2,null]")]
    [InlineData("{\"s\":0}")]
    public void Phrase_RejectsTheWrongShape(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PhraseDto>(json));
    }

    [Theory]
    [InlineData("""{"id": "0", "tokens": []}""")]
    [InlineData("""{"id": "0", "tokens": [], "phrases": []}""")]
    public void Block_WithoutPhrases_HasAnEmptyList(string json)
    {
        Assert.Empty(JsonSerializer.Deserialize<TokenizedBlockDto>(json)!.Phrases);
    }
}
