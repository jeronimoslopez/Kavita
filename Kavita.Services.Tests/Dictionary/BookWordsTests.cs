using Kavita.Models.DTOs.Dictionary;
using Kavita.Services.Dictionary;

namespace Kavita.Services.Tests.Dictionary;

public class BookWordsTests
{
    /// <summary>
    /// A block of consecutive tokens from (surface, reading) pairs, with one span over the given tokens
    /// </summary>
    /// <param name="first">The first covered token</param>
    /// <param name="last">The last covered token</param>
    private static IReadOnlyList<BookWord> Detect(int first, int last, string reading, params (string Surface, string? R)[] pieces)
    {
        var tokens = new List<TokenDto>();
        var offset = 0;
        foreach (var (surface, r) in pieces)
        {
            tokens.Add(new TokenDto(offset, offset + surface.Length, 1, r, null));
            offset += surface.Length;
        }
        var text = string.Concat(pieces.Select(p => p.Surface));
        return BookWords.Detect(text, tokens, [new RubyDto(tokens[first].S, tokens[last].E, reading)]);
    }

    private static IReadOnlyList<BookWord> DetectAll(string reading, params (string Surface, string? R)[] pieces) =>
        Detect(0, pieces.Length - 1, reading, pieces);

    [Theory]
    [InlineData("翠", "みどり", "苓", null, "スイレイ", "すいれい")]
    [InlineData("馬", "うま", "閃", "せん", "バセン", "ばせん")]
    // The book's furigana equals the pieces' readings, and the second rule takes them as names of single kanji
    [InlineData("羅", "ら", "半", "はん", "ラハン", "らはん")]
    [InlineData("素", "そ", "貞", "てい", "ソテイ", "そてい")]
    [InlineData("髪", "かみ", "紐", "ひも", "かみひも", "かみひも")]
    public void Detect_TwoTokensUnderOneSpan_IsABookWord(string a, string? ra, string b, string? rb, string printed,
        string reading)
    {
        var word = Assert.Single(DetectAll(printed, (a, ra), (b, rb)));

        Assert.Equal(new BookWord(a + b, reading, printed), word);
    }

    [Fact]
    public void Detect_AReadingThatIsNotThePiecesReading_IsABookWord()
    {
        Assert.Equal([new BookWord("果実水", "じゅーす", "ジュース")], DetectAll("ジュース", ("果実", "かじつ"), ("水", "みず")));
    }

    [Theory]
    [InlineData("だっしゅつほうほう", "脱出", "だっしゅつ", "方法", "ほうほう")]
    // ふか read ぶか after the first piece
    [InlineData("きょうみぶか", "興味", "きょうみ", "深", "ふか")]
    // く before a small っ
    [InlineData("がっこうない", "学校", "がっこう", "内", "ない")]
    // Small kana printed large
    [InlineData("きようみぶか", "興味", "きょうみ", "深", "ふか")]
    // Two characters before the second token, so the single-kanji rule doesn't apply
    [InlineData("ろくしょうかん", "緑青", "ろくしょう", "館", "かん")]
    public void Detect_AnOrdinaryCompoundPrintedWithOneSpan_IsNotABookWord(string printed, string a, string ra, string b,
        string rb)
    {
        Assert.Empty(DetectAll(printed, (a, ra), (b, rb)));
    }

    [Theory]
    [InlineData("てっせい", "てつ", "せい")]
    [InlineData("がっこう", "がく", "こう")]
    [InlineData("きょうみぶか", "きょうみ", "ふか")]
    [InlineData("ラハン", "ら", "はん")]
    [InlineData("じゅうななじ", "じゅうなな", "じ")]
    public void IsPiecesReading_IgnoresSmallKanaRendakuAndSokuon(string reading, string a, string b)
    {
        Assert.True(BookWords.IsPiecesReading([a, b], MarkedReading.ToHiragana(reading)));
    }

    [Theory]
    [InlineData("すいれい", "みどり", "苓")]
    [InlineData("じゅーす", "かじつ", "みず")]
    [InlineData("らは", "ら", "はん")]
    // A voiced first kana is allowed only after the first piece
    [InlineData("ばせん", "は", "せん")]
    public void IsPiecesReading_IsFalseForAnyOtherReading(string reading, string a, string b)
    {
        Assert.False(BookWords.IsPiecesReading([a, b], reading));
    }

    /// <summary>
    /// 鉄製 and 学校 read as their pieces, which the first rule rejects. Each is two single kanji not starting with a
    /// counter, so the single-kanji rule takes them, the way it takes 髪紐
    /// </summary>
    [Theory]
    [InlineData("てっせい", "鉄", "てつ", "製", "せい")]
    [InlineData("がっこう", "学", "がく", "校", "こう")]
    public void Detect_TwoSingleKanjiReadAsTheirPieces_IsABookWordByTheSecondRule(string printed, string a, string ra,
        string b, string rb)
    {
        Assert.Equal([new BookWord(a + b, printed, printed)], DetectAll(printed, (a, ra), (b, rb)));
    }

    [Theory]
    [InlineData("おくえん", "億", "おく", "円", "えん")]
    [InlineData("ひとくみ", "１", "いち", "組", "くみ")]
    [InlineData("にじゅうえん", "20", "にじゅう", "円", "えん")]
    public void Detect_ASpanOverANumeral_IsNotABookWord(string printed, string a, string ra, string b, string rb)
    {
        Assert.Empty(DetectAll(printed, (a, ra), (b, rb)));
    }

    [Fact]
    public void Detect_SingleKanjiAfterANumber_IsNotABookWord()
    {
        Assert.Empty(Detect(1, 2, "ねんまえ", ("4", "よん"), ("年", "ねん"), ("前", "まえ")));
        Assert.Empty(Detect(1, 2, "らはん", ("４", "よん"), ("羅", "ら"), ("半", "はん")));
        // The same name after a word that isn't a number qualifies
        Assert.Single(Detect(1, 2, "らはん", ("と", "と"), ("羅", "ら"), ("半", "はん")));
    }

    [Theory]
    [InlineData("回", "かい", "目", "め", "かいめ")]
    [InlineData("人", "にん", "前", "まえ", "にんまえ")]
    public void Detect_SingleKanjiStartingWithACounter_IsNotABookWord(string a, string ra, string b, string rb, string printed)
    {
        Assert.Empty(DetectAll(printed, (a, ra), (b, rb)));
    }

    [Fact]
    public void Detect_ASpanOverOneToken_IsNotABookWord()
    {
        Assert.Empty(Detect(1, 1, "スイ", ("あ", null), ("翠", "みどり"), ("苓", null)));
    }

    [Fact]
    public void Detect_ASpanThatEndsOrStartsInsideAToken_IsNotABookWord()
    {
        var tokens = new List<TokenDto> { new(0, 3, 1, "りーしゅひ", null), new(3, 4, 2, "は", null) };

        Assert.Empty(BookWords.Detect("里樹妃は", tokens, [new RubyDto(0, 2, "リーシュ")]));
        Assert.Empty(BookWords.Detect("里樹妃は", tokens, [new RubyDto(1, 4, "じゅひは")]));
    }

    [Fact]
    public void Detect_ASpanWithoutKanjiOrWithAReadingThatIsNotKana_IsNotABookWord()
    {
        Assert.Empty(DetectAll("ゆめ", ("ゆ", null), ("め", null)));
        Assert.Empty(DetectAll("・・", ("翠", null), ("苓", null)));
    }

    [Fact]
    public void Detect_OverlappingSpans_DefineNothing()
    {
        var tokens = new List<TokenDto> { new(0, 1, 1, null, null), new(1, 2, 2, null, null) };

        Assert.Empty(BookWords.Detect("翠苓", tokens, [new RubyDto(0, 2, "スイレイ"), new RubyDto(0, 1, "スイ")]));
    }

    [Fact]
    public void Detect_FindsEverySpanOfABlockInTextOrder()
    {
        // 翠苓と馬閃
        var tokens = new List<TokenDto>
        {
            new(0, 1, 1, null, null), new(1, 2, 2, null, null), new(2, 3, 3, "と", null),
            new(3, 4, 4, "うま", null), new(4, 5, 5, "せん", null),
        };

        var words = BookWords.Detect("翠苓と馬閃", tokens, [new RubyDto(3, 5, "バセン"), new RubyDto(0, 2, "スイレイ")]);

        Assert.Equal(["翠苓", "馬閃"], words.Select(w => w.Text));
    }

    [Fact]
    public void Detect_ASurrogatePairKanji_CountsAsOneCharacter()
    {
        // 𠮟 is two UTF-16 units
        var tokens = new List<TokenDto> { new(0, 2, 1, "しつ", null), new(2, 3, 2, "せき", null) };

        Assert.Equal([new BookWord("𠮟責", "しっせき", "しっせき")], BookWords.Detect("𠮟責", tokens, [new RubyDto(0, 3, "しっせき")]));
    }

    /// <summary>
    /// The book words of one paragraph of markup, with its text cut into tokens of the given lengths and readings
    /// </summary>
    private static IReadOnlyList<BookWord> DetectMarkup(string paragraph, params (int Length, string? R)[] pieces)
    {
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml($"<html><body>{paragraph}</body></html>");
        var block = Assert.Single(new EpubTextBlocks().GetBlocks(doc.DocumentNode.SelectSingleNode("//body")));

        var tokens = new List<TokenDto>();
        var offset = 0;
        foreach (var (length, r) in pieces)
        {
            tokens.Add(new TokenDto(offset, offset + length, 1, r, null));
            offset += length;
        }
        Assert.Equal(block.Text.Length, offset);
        return BookWords.Detect(block.Text, tokens, block.Ruby);
    }

    [Fact]
    public void Detect_OneRubyElementWithAReadingPerKanji_IsOneBookWord()
    {
        var words = DetectMarkup("<p><ruby><rb>羅</rb><rt>ラ</rt><rb>半</rb><rt>ハン</rt></ruby>は</p>",
            (1, "ら"), (1, "はん"), (1, "は"));

        Assert.Equal([new BookWord("羅半", "らはん", "ラハン")], words);
    }

    [Fact]
    public void Detect_OneRubyElementWithoutRb_IsOneBookWord()
    {
        var words = DetectMarkup("<p><ruby>馬<rt>バ</rt>閃<rt>セン</rt></ruby>が</p>", (1, "うま"), (1, "せん"), (1, "が"));

        Assert.Equal([new BookWord("馬閃", "ばせん", "バセン")], words);
    }

    [Fact]
    public void Detect_ARubyElementPerKanji_JoinsNothing()
    {
        Assert.Empty(DetectMarkup("<p><ruby>羅<rt>ら</rt></ruby><ruby>半<rt>はん</rt></ruby>は</p>",
            (1, "ら"), (1, "はん"), (1, "は")));
    }

    [Fact]
    public void Detect_AGroupOverOneToken_IsNotABookWord()
    {
        Assert.Empty(DetectMarkup("<p><ruby>高<rt>ガオ</rt>順<rt>シュン</rt></ruby>は</p>", (2, "こうじゅん"), (1, "は")));
    }

    [Fact]
    public void Detect_AGroupWithAGap_FallsBackToItsSpansOnTheirOwn()
    {
        // と sits between the group's two spans unmarked, so the group isn't one run of text
        var tokens = new List<TokenDto> { new(0, 1, 1, null, null), new(1, 2, 2, null, null), new(2, 3, 3, null, null) };
        RubyDto[] ruby = [new(0, 1, "スイ", 0), new(2, 3, "レイ", 0)];

        Assert.Empty(BookWords.Detect("翠と苓", tokens, ruby));
    }

    [Fact]
    public void Detect_AGroupInsideAnotherSpan_FallsBackToTheSpansOnTheirOwn()
    {
        // An outer ruby over 翠苓 overlaps the group, so neither joins, as overlapping spans never do
        var tokens = new List<TokenDto> { new(0, 1, 1, null, null), new(1, 2, 2, null, null) };
        RubyDto[] ruby = [new(0, 1, "スイ", 0), new(1, 2, "レイ", 0), new(0, 2, "スイレイ")];

        Assert.Empty(BookWords.Detect("翠苓", tokens, ruby));
    }

    [Fact]
    public void Detect_AGroupWithANonKanaReading_FallsBackToTheSpansOnTheirOwn()
    {
        var tokens = new List<TokenDto> { new(0, 1, 1, null, null), new(1, 2, 2, null, null), new(2, 3, 3, null, null) };
        RubyDto[] ruby = [new(0, 2, "スイレイ", 0), new(2, 3, "・", 0)];

        // The kana span still qualifies on its own, over 翠 and 苓
        Assert.Equal(["翠苓"], BookWords.Detect("翠苓様", tokens, ruby).Select(w => w.Text));
    }

    [Fact]
    public void Detect_AGroupOfOrdinaryWordsReadAsTheirPieces_IsNotABookWord()
    {
        // 漢字 read かんじ is the pieces' reading and the pieces aren't all one kanji each
        Assert.Empty(DetectMarkup("<p><ruby>漢字<rt>かんじ</rt>書<rt>か</rt></ruby>く</p>", (2, "かんじ"), (1, "か"), (1, "く")));
    }

    [Theory]
    [InlineData("4", true)]
    [InlineData("１２", true)]
    [InlineData("三十", true)]
    [InlineData("〇", true)]
    [InlineData("4月", false)]
    [InlineData("翠", false)]
    [InlineData("", false)]
    public void IsNumerals_TakesDigitsAndKanjiNumeralsOnly(string text, bool expected)
    {
        Assert.Equal(expected, BookWords.IsNumerals(text, 0, text.Length));
    }
}
