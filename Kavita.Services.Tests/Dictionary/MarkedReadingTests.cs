using Kavita.Models.DTOs.Dictionary;
using Kavita.Services.Dictionary;

namespace Kavita.Services.Tests.Dictionary;

public class MarkedReadingTests
{
    private static TokenDto Token(int s, int e) => new(s, e, 1, null, null);

    private static MarkedReadingResult? Of(string text, int s, int e, string? lemma, params RubyDto[] ruby) =>
        MarkedReading.Of(text, Token(s, e), ruby, lemma);

    #region Cases from real books

    [Fact]
    public void InflectedVerb_TakesTheLemmasTrailingKana()
    {
        Assert.Equal(new MarkedReadingResult("あか", "あか", "あく", "あく"),
            Of("戸を開かない", 2, 4, "開く", new RubyDto(2, 3, "あ")));
    }

    [Fact]
    public void JukujikunOverTwoKanji_IsTheWholeReading()
    {
        Assert.Equal(new MarkedReadingResult("ねえちゃん", "ねえちゃん", "ねえちゃん", "ねえちゃん"),
            Of("小姐が", 0, 2, "小姐", new RubyDto(0, 2, "ねえちゃん")));
    }

    [Fact]
    public void SurfaceThatIsTheLemma_GivesTheSameReadingTwice()
    {
        Assert.Equal(new MarkedReadingResult("いう", "いう", "いう", "いう"),
            Of("言う", 0, 2, "言う", new RubyDto(0, 1, "い")));
    }

    [Fact]
    public void GeminateStem_TakesTheLemmasEnding()
    {
        Assert.Equal(new MarkedReadingResult("いっ", "いっ", "いう", "いう"),
            Of("言った", 0, 2, "言う", new RubyDto(0, 1, "い")));
    }

    [Fact]
    public void KatakanaName_KeepsThePrintedFormBesideTheHiragana()
    {
        Assert.Equal(new MarkedReadingResult("ガオシュン", "がおしゅん", "ガオシュン", "がおしゅん"),
            Of("高順は", 0, 2, "高順", new RubyDto(0, 2, "ガオシュン")));
    }

    [Fact]
    public void IchidanStem_TakesTheLemmasWholeEnding()
    {
        Assert.Equal(new MarkedReadingResult("たべ", "たべ", "たべる", "たべる"),
            Of("食べた", 0, 2, "食べる", new RubyDto(0, 1, "た")));
    }

    [Fact]
    public void TokenOnlyPartlyCovered_IsNotMarked()
    {
        Assert.Null(Of("漢字", 0, 2, "漢字", new RubyDto(0, 1, "かん")));
    }

    [Fact]
    public void SpanCrossingTheTokensEnd_IsNotMarked()
    {
        Assert.Null(Of("漢字", 0, 1, "漢", new RubyDto(0, 2, "かんじ")));
    }

    #endregion

    [Fact]
    public void SpanCrossingTheTokensStart_IsNotMarked()
    {
        Assert.Null(Of("漢字", 1, 2, "字", new RubyDto(0, 2, "かんじ")));
    }

    [Fact]
    public void OneSpanPerKanji_JoinInOrder()
    {
        var ruby = new[] { new RubyDto(4, 5, "じ"), new RubyDto(3, 4, "かん") };

        Assert.Equal(new MarkedReadingResult("かんじ", "かんじ", "かんじ", "かんじ"),
            MarkedReading.Of("これは漢字だ", Token(3, 5), ruby, "漢字"));
    }

    [Fact]
    public void KanaBetweenAndAroundSpans_IsReadAsWritten()
    {
        Assert.Equal(new MarkedReadingResult("おちゃ", "おちゃ", "おちゃ", "おちゃ"),
            Of("お茶", 0, 2, "お茶", new RubyDto(1, 2, "ちゃ")));
        Assert.Equal(new MarkedReadingResult("ひきだし", "ひきだし", "ひきだす", "ひきだす"),
            Of("引き出し", 0, 4, "引き出す", new RubyDto(0, 1, "ひ"), new RubyDto(2, 3, "だ")));
    }

    [Fact]
    public void KatakanaInTheSurface_IsHiraganaInTheReading()
    {
        Assert.Equal(new MarkedReadingResult("ばかヤロウ", "ばかやろう", "ばかヤロウ", "ばかやろう"),
            Of("馬鹿ヤロウ", 0, 5, "馬鹿ヤロウ", new RubyDto(0, 2, "ばか")));
    }

    [Fact]
    public void StemThatDiffersFromTheLemmas_HasNoDictionaryForm()
    {
        Assert.Equal(new MarkedReadingResult("いっ", "いっ", null, null),
            Of("云った", 0, 2, "言う", new RubyDto(0, 1, "い")));
    }

    [Fact]
    public void NoLemma_HasNoDictionaryForm()
    {
        Assert.Equal(new MarkedReadingResult("あか", "あか", null, null), Of("開か", 0, 2, null, new RubyDto(0, 1, "あ")));
        Assert.Equal(new MarkedReadingResult("あか", "あか", null, null), Of("開か", 0, 2, "", new RubyDto(0, 1, "あ")));
    }

    [Fact]
    public void KanaUnderASpan_AreStemAndNotTrailingKana()
    {
        // The span covers the kana, so the stem is さま, which isn't the lemma's stem
        Assert.Equal(new MarkedReadingResult("さま", "さま", null, null),
            Of("さま", 0, 2, "様", new RubyDto(0, 2, "さま")));
    }

    [Fact]
    public void TokenWithoutRuby_IsNotMarked()
    {
        Assert.Null(Of("ひらがな", 0, 4, "ひらがな"));
        Assert.Null(Of("漢字とかな", 2, 5, "とかな", new RubyDto(0, 2, "かんじ")));
    }

    [Fact]
    public void EmphasisDots_CountAsNoSpan()
    {
        Assert.Null(Of("それ", 0, 2, "それ", new RubyDto(0, 1, "・"), new RubyDto(1, 2, "・")));
        Assert.Null(Of("それ", 0, 2, "それ", new RubyDto(0, 2, "﹅﹅")));
        // A dot over a neighbouring token doesn't stop this one being read
        Assert.Equal("かんじ", Of("漢字それ", 0, 2, "漢字", new RubyDto(0, 2, "かんじ"), new RubyDto(1, 3, "・"))?.Reading);
    }

    [Fact]
    public void KanjiUnderANonKanaReading_IsNotMarked()
    {
        Assert.Null(Of("漢字", 0, 2, "漢字", new RubyDto(0, 2, "kanji")));
    }

    [Fact]
    public void IterationMark_IsAKanji()
    {
        Assert.Equal("ひとびと", Of("人々", 0, 2, "人々", new RubyDto(0, 2, "ひとびと"))?.Reading);
        Assert.Null(Of("人々", 0, 2, "人々", new RubyDto(0, 1, "ひと")));
        Assert.Null(Of("〆る", 0, 2, "〆る"));
    }

    [Fact]
    public void SurrogatePairKanji_IsCoveredByItsSpan()
    {
        Assert.Equal(new MarkedReadingResult("しかっ", "しかっ", "しかる", "しかる"),
            Of("𠮟った", 0, 3, "𠮟る", new RubyDto(0, 2, "しか")));
        Assert.Null(Of("𠮟った", 0, 3, "𠮟る"));
        Assert.Null(Of("𠮟った", 0, 3, "𠮟る", new RubyDto(2, 3, "っ")));
    }

    [Fact]
    public void OverlappingSpans_AreNotMarked()
    {
        Assert.Null(Of("明日", 0, 2, "明日", new RubyDto(0, 1, "あ"), new RubyDto(0, 2, "あした")));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 3)]
    [InlineData(1, 1)]
    public void TokenOutsideTheText_IsNotMarked(int s, int e)
    {
        Assert.Null(Of("漢字", s, e, "漢字", new RubyDto(0, 2, "かんじ")));
    }

    [Theory]
    [InlineData("カタカナ", "かたかな")]
    [InlineData("ヴァイオリン", "ゔぁいおりん")]
    [InlineData("コーヒー", "こーひー")]
    [InlineData("ヽヾ", "ゝゞ")]
    [InlineData("ひらがな", "ひらがな")]
    [InlineData("ヷ", "ヷ")]
    public void ToHiragana_MapsKatakanaAndLeavesTheRest(string value, string expected)
    {
        Assert.Equal(expected, MarkedReading.ToHiragana(value));
    }
}
