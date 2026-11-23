using System.Text.Json;
using Kavita.Models.DTOs.Dictionary;

namespace Kavita.Services.Tests.Dictionary;

public class TokenizeContractTests
{
    /// <summary>
    /// The hand-written sample of contract A, which the real sidecar's output replaces at deploy time
    /// </summary>
    private static readonly string SampleResponse = File.ReadAllText(
        Path.Join(Directory.GetCurrentDirectory(), "../../../../text-sidecar-contract/sample-response.json"));

    [Fact]
    public void SampleResponse_Deserializes()
    {
        var response = JsonSerializer.Deserialize<TokenizeResponseDto>(SampleResponse);

        Assert.NotNull(response);
        Assert.StartsWith("sudachipy=", response.Version);
        Assert.Equal(["0", "1"], response.Blocks.Select(b => b.Id));

        var spicy = response.Words[0];
        Assert.Equal("辛い", spicy.Lemma);
        Assert.Equal(["からい", "つらい"], spicy.Readings);
        Assert.Equal("1584120:1", spicy.Senses[0].Key);
        Assert.Equal(["spicy", "hot"], spicy.Senses[0].Gloss);
        Assert.True(spicy.IsAmbiguous);
        Assert.False(response.Words[1].IsAmbiguous);
        Assert.Null(response.Words[3].Jlpt);

        var first = response.Blocks[0].Tokens;
        Assert.Equal(new TokenDto(0, 2, 0, "からい", null), first[0]);
        Assert.Equal(new TokenDto(5, 6, null, "を", null), first[2]);
        Assert.Equal(new TokenDto(9, 10, null, null, null), first[5]);

        var compound = response.Blocks[1].Tokens[0];
        Assert.Equal((0, 5, 3L, "こくりつとしょかん"), (compound.S, compound.E, compound.W, compound.R));
        Assert.NotNull(compound.Sub);
        Assert.Equal([new SubTokenDto(0, 2, 4, "こくりつ"), new SubTokenDto(2, 4, 5, "としょ"), new SubTokenDto(4, 5, 6, "かん")],
            compound.Sub);
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
}
