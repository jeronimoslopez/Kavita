using HtmlAgilityPack;
using Kavita.API.Services;
using Kavita.Models.DTOs.Dictionary;
using Kavita.Services.Dictionary;

namespace Kavita.Services.Tests.Dictionary;

public class EpubTextBlocksTests
{
    private readonly EpubTextBlocks _blocks = new();

    private static HtmlNode Body(string bodyHtml)
    {
        var doc = new HtmlDocument { OptionFixNestedTags = true };
        doc.LoadHtml($"<html><head><title>t</title></head><body>{bodyHtml}</body></html>");
        return doc.DocumentNode.SelectSingleNode("//body");
    }

    private List<string> Texts(string bodyHtml) => _blocks.GetBlocks(Body(bodyHtml)).Select(b => b.Text).ToList();

    [Fact]
    public void Paragraphs_AreBlocksInDocumentOrder()
    {
        var blocks = _blocks.GetBlocks(Body("<p>one</p><p>two</p><h2>three</h2>"));

        Assert.Equal([0, 1, 2], blocks.Select(b => b.Index));
        Assert.Equal(["one", "two", "three"], blocks.Select(b => b.Text));
        Assert.Equal(["p", "p", "h2"], blocks.Select(b => b.Node.Name));
    }

    [Fact]
    public void NestedBlocks_OnlyTheInnermostCount()
    {
        var blocks = _blocks.GetBlocks(Body(
            "<section><div><p>a</p><blockquote><p>b</p></blockquote></div><div>c</div></section>"));

        Assert.Equal(["a", "b", "c"], blocks.Select(b => b.Text));
        Assert.Equal(["p", "p", "div"], blocks.Select(b => b.Node.Name));
    }

    [Fact]
    public void InlineElements_AreFollowedInto()
    {
        Assert.Equal(["Say hello, world now"],
            Texts("<p>Say <em>hel<b>lo</b></em>, <a href='#x'><span>world</span></a> now</p>"));
    }

    [Fact]
    public void LooseText_NextToBlocks_BelongsToNoBlock()
    {
        var blocks = _blocks.GetBlocks(Body("<div>intro <p>para</p> tail<span>more</span></div>"));

        Assert.Equal(["para"], blocks.Select(b => b.Text));
    }

    [Fact]
    public void BodyWithoutBlocks_IsItselfTheBlock()
    {
        var blocks = _blocks.GetBlocks(Body("本文だけ<br/>二行目"));

        var block = Assert.Single(blocks);
        Assert.Equal("body", block.Node.Name);
        Assert.Equal("本文だけ二行目", block.Text);
    }

    [Fact]
    public void Ruby_SkipsRtAndRp()
    {
        Assert.Equal(["吾輩は猫である", "始まり"], Texts(
            "<p><ruby><rb>吾輩</rb><rt>わがはい</rt></ruby>は猫である</p>" +
            "<p><ruby>始<rp>（</rp><rt>はじ</rt><rp>）</rp></ruby>まり</p>"));
    }

    #region Ruby spans

    private EpubTextBlock Block(string bodyHtml) => Assert.Single(_blocks.GetBlocks(Body(bodyHtml)));

    /// <summary>
    /// Each case's block text, which must be the text the block had before ruby spans existed, and its spans
    /// </summary>
    public static TheoryData<string, string, RubyDto[]> RubyShapes => new()
    {
        // One ruby over its whole base
        { "<p><ruby>漢字<rt>かんじ</rt></ruby>を書く</p>", "漢字を書く", [new RubyDto(0, 2, "かんじ")] },
        // One span per rb and rt pair
        { "<p><ruby><rb>漢</rb><rt>かん</rt><rb>字</rb><rt>じ</rt></ruby></p>", "漢字",
            [new RubyDto(0, 1, "かん"), new RubyDto(1, 2, "じ")] },
        // Every rb first and every rt after, paired in order
        { "<p><ruby><rb>漢</rb><rb>字</rb><rt>かん</rt><rt>じ</rt></ruby></p>", "漢字",
            [new RubyDto(0, 1, "かん"), new RubyDto(1, 2, "じ")] },
        // Bases without rb, each up to its rt
        { "<p><ruby>漢<rt>かん</rt>字<rt>じ</rt></ruby></p>", "漢字", [new RubyDto(0, 1, "かん"), new RubyDto(1, 2, "じ")] },
        // rp fallbacks are in neither the text nor the reading
        { "<p><ruby>始<rp>（</rp><rt>はじ</rt><rp>）</rp></ruby>まり</p>", "始まり", [new RubyDto(0, 1, "はじ")] },
        { "<p><ruby>漢<rp>(</rp><rt>かん</rt><rp>)</rp>字<rp>(</rp><rt>じ</rt><rp>)</rp></ruby></p>", "漢字",
            [new RubyDto(0, 1, "かん"), new RubyDto(1, 2, "じ")] },
        // Markup inside rt is read through
        { "<p><ruby>東京<rt><span class='k'>とう</span><b>きょう</b></rt></ruby></p>", "東京", [new RubyDto(0, 2, "とうきょう")] },
        // An empty rt, or one of whitespace, gives no span
        { "<p><ruby>空<rt></rt></ruby>と<ruby>白<rt> </rt></ruby></p>", "空と白", [] },
        // A ruby inside inline elements inside the block, after other text
        { "<p>前<span><em>と<ruby>後<rt>うしろ</rt></ruby></em></span>だ</p>", "前と後だ", [new RubyDto(2, 3, "うしろ")] },
        // A surrogate pair before the ruby counts as two UTF-16 units
        { "<p>𠮷<ruby>野家<rt>のや</rt></ruby></p>", "𠮷野家", [new RubyDto(2, 4, "のや")] },
        { "<p><ruby>𠮟<rt>しか</rt></ruby>る</p>", "𠮟る", [new RubyDto(0, 2, "しか")] },
        // A ruby whose base is empty or only whitespace gives no span
        { "<p>あ<ruby><rt>から</rt></ruby>い<ruby> <rt>そら</rt></ruby></p>", "あい ", [] },
        // Whitespace around the base or the reading belongs to neither
        { "<p><ruby>\n 猫 <rt> ねこ </rt></ruby></p>", "\n 猫 ", [new RubyDto(2, 3, "ねこ")] },
        // A katakana reading stays as printed
        { "<p><ruby>高順<rt>ガオシュン</rt></ruby>は</p>", "高順は", [new RubyDto(0, 2, "ガオシュン")] },
        // A ruby nested in another's base reads its own rt, and the outer one covers both
        { "<p><ruby><ruby>明<rt>あ</rt></ruby>日<rt>あした</rt></ruby></p>", "明日",
            [new RubyDto(0, 1, "あ"), new RubyDto(0, 2, "あした")] },
        // An rtc holds the rt elements of the ruby
        { "<p><ruby>漢字<rtc><rt>かんじ</rt></rtc></ruby></p>", "漢字", [new RubyDto(0, 2, "かんじ")] },
        // An rb left without an rt gives no span
        { "<p><ruby><rb>漢</rb><rb>字</rb><rt>かん</rt></ruby></p>", "漢字", [new RubyDto(0, 1, "かん")] },
        // Entities decode in the reading as in the text
        { "<p><ruby>&#x5B57;<rt>&#x3058;</rt></ruby></p>", "字", [new RubyDto(0, 1, "じ")] },
    };

    [Theory]
    [MemberData(nameof(RubyShapes))]
    public void Ruby_GivesSpansOverTheBaseAndLeavesTheTextAlone(string html, string text, RubyDto[] ruby)
    {
        var block = Block(html);

        Assert.Equal(text, block.Text);
        Assert.Equal(ruby, block.Ruby);
    }

    [Fact]
    public void Ruby_StrayRtOutsideARubyGivesNoSpan()
    {
        var block = Block("<p>猫<rt>ねこ</rt>だ</p>");

        Assert.Equal("猫だ", block.Text);
        Assert.Empty(block.Ruby);
    }

    [Fact]
    public void Ruby_SpansBelongToTheirOwnBlock()
    {
        var blocks = _blocks.GetBlocks(Body(
            "<p><ruby>一<rt>いち</rt></ruby></p><p>に<ruby>二<rt>に</rt></ruby></p><p>三</p>"));

        Assert.Equal(["一", "に二", "三"], blocks.Select(b => b.Text));
        Assert.Equal([new RubyDto(0, 1, "いち")], blocks[0].Ruby);
        Assert.Equal([new RubyDto(1, 2, "に")], blocks[1].Ruby);
        Assert.Empty(blocks[2].Ruby);
    }

    [Fact]
    public void Ruby_StampBlocksFindsTheSameSpans()
    {
        const string html = "<p><ruby><rb>漢</rb><rt>かん</rt><rb>字</rb><rt>じ</rt></ruby>を</p>";

        Assert.Equal(_blocks.GetBlocks(Body(html))[0].Ruby, _blocks.StampBlocks(Body(html))[0].Ruby);
    }

    #endregion

    [Fact]
    public void Entities_DecodeLikeABrowser()
    {
        Assert.Equal(["a & b < c > \"d\" 'e'", "x y", "あい", "\U0001F431 and \U0001F431", "© —"], Texts(
            "<p>a &amp; b &lt; c &gt; &quot;d&quot; &apos;e&apos;</p>" +
            "<p>x&nbsp;y</p>" +
            "<p>&#12354;&#x3044;</p>" +
            "<p>&#x1F431; and &#128049;</p>" +
            "<p>&copy; &mdash;</p>"));
    }

    [Fact]
    public void NumericReferences_FollowTheHtmlSpec()
    {
        // Windows-1252 remapping, U+FFFD for null, surrogates and out of range, and an optional semicolon
        Assert.Equal(["— € \u0081", "� � �", "AB", "&# &#x; &#xZ"], Texts(
            "<p>&#151; &#x80; &#129;</p>" +
            "<p>&#0; &#xD800; &#x110000;</p>" +
            "<p>&#65&#x42</p>" +
            "<p>&# &#x; &#xZ</p>"));
    }

    [Fact]
    public void UnknownOrBareAmpersands_StayAsWritten()
    {
        Assert.Equal(["R&D &bogus; & more &a&b;"], Texts("<p>R&D &bogus; & more &a&b;</p>"));
    }

    [Fact]
    public void Br_AddsNoText()
    {
        Assert.Equal(["onetwo"], Texts("<p>one<br/>two</p>"));
    }

    [Fact]
    public void EmptyBlocks_KeepTheirIndex()
    {
        var blocks = _blocks.GetBlocks(Body("<p>a</p><p><br/></p><div></div><p><img src='x.png'/></p><p>b</p>"));

        Assert.Equal([0, 1, 2, 3, 4], blocks.Select(b => b.Index));
        Assert.Equal(["a", "", "", "", "b"], blocks.Select(b => b.Text));
    }

    [Fact]
    public void Whitespace_IsKept()
    {
        Assert.Equal(["  a \t b\n c  ", "　全角"], Texts("<p>  a \t b\n c  </p><p>　全角</p>"));
    }

    [Fact]
    public void LineEndings_BecomeLf()
    {
        Assert.Equal(["a\nb\nc"], Texts("<p>a\r\nb\rc</p>"));
    }

    [Fact]
    public void NulCharacters_AreDropped()
    {
        Assert.Equal(["ab\uFFFD"], Texts("<p>a\0b&#0;</p>"));
    }

    [Fact]
    public void Pre_DropsTheNewlineRightAfterItsStartTag()
    {
        Assert.Equal(["line1\n  line2\n", "\nkept", "\n\nfirst dropped", "listed"], Texts(
            "<pre>\nline1\n  line2\n</pre>" +
            "<pre><code>\nkept</code></pre>" +
            "<pre>\n\n\nfirst dropped</pre>" +
            "<p><listing>\nlisted</listing></p>"));
    }

    [Fact]
    public void Tables_CellsAreBlocks()
    {
        var blocks = _blocks.GetBlocks(Body(
            "<table><caption>Cap</caption><thead><tr><th>H1</th><th>H2</th></tr></thead>" +
            "<tbody><tr><td>a</td><td><p>b1</p><p>b2</p></td></tr></tbody></table>"));

        Assert.Equal(["Cap", "H1", "H2", "a", "b1", "b2"], blocks.Select(b => b.Text));
        Assert.Equal(["caption", "th", "th", "td", "p", "p"], blocks.Select(b => b.Node.Name));
    }

    [Fact]
    public void Lists_ItemsAreBlocks_AndNestedListsSplitTheirParent()
    {
        var blocks = _blocks.GetBlocks(Body(
            "<ul><li>one</li><li>two<ul><li>two.a</li></ul></li></ul><dl><dt>term</dt><dd>def</dd></dl>"));

        // "two" sits next to a nested list, so it's loose text
        Assert.Equal(["one", "two.a", "term", "def"], blocks.Select(b => b.Text));
    }

    [Fact]
    public void ScriptAndStyle_AreSkipped()
    {
        Assert.Equal(["before after"], Texts(
            "<p>before <script>var x = '<p>';</script><style>p{color:red}</style>after</p>"));
    }

    [Fact]
    public void NoscriptAndTemplate_HoldNoBlocksAndNoText()
    {
        var blocks = _blocks.GetBlocks(Body(
            "<noscript><p>hidden</p></noscript><template><p>inert</p></template><p>a<noscript>b</noscript>c</p>"));

        Assert.Equal(["ac"], blocks.Select(b => b.Text));
    }

    [Fact]
    public void Comments_AreSkipped()
    {
        Assert.Equal(["ab"], Texts("<p>a<!-- note -->b</p>"));
    }

    [Fact]
    public void AstralCharacters_CountAsTwoUtf16Units()
    {
        var block = Assert.Single(_blocks.GetBlocks(Body("<p>𠮷野家🐱</p>")));

        Assert.Equal("𠮷野家🐱", block.Text);
        Assert.Equal(6, block.Text.Length);
    }

    [Fact]
    public void StampBlocks_SetsTheAttributeAndAddsNoElement()
    {
        const string html = "<div><p id='a'>one</p><p>two<span>!</span></p></div><ul><li>x</li></ul>";
        var body = Body(html);
        var before = body.Descendants().Select(n => n.XPath).ToList();

        var stamped = _blocks.StampBlocks(body);

        Assert.Equal(before, body.Descendants().Select(n => n.XPath).ToList());
        Assert.Equal(["0", "1", "2"], stamped.Select(b => b.Node.GetAttributeValue(IEpubTextBlocks.BlockAttribute, "")));
        Assert.Equal("a", stamped[0].Node.Id);
        Assert.Equal(3, body.Descendants().Count(n => n.Attributes.Contains(IEpubTextBlocks.BlockAttribute)));
        Assert.DoesNotContain(body.Descendants(), n => n.Id.Length > 0 && n.Id != "a");
    }

    [Fact]
    public void StampBlocks_MatchesGetBlocks()
    {
        const string html = "<section><h1>T</h1><div>loose<p>a</p></div><p></p><table><tr><td>c</td></tr></table></section>";

        var found = _blocks.GetBlocks(Body(html));
        var stamped = _blocks.StampBlocks(Body(html));

        Assert.Equal(found.Select(b => (b.Index, b.Node.XPath, b.Text)), stamped.Select(b => (b.Index, b.Node.XPath, b.Text)));
    }

    [Fact]
    public void StampBlocks_ReplacesValuesTheBookCarries()
    {
        var body = Body("<div data-kb='7'><p data-kb='9'>a</p></div><p>b</p>");

        _blocks.StampBlocks(body);

        Assert.Equal([("div", ""), ("p", "0"), ("p", "1")], body.Descendants().Where(n => n.NodeType == HtmlNodeType.Element)
            .Select(n => (n.Name, n.GetAttributeValue(IEpubTextBlocks.BlockAttribute, ""))));
    }

    [Fact]
    public void GetBlocks_LeavesTheDocumentAlone()
    {
        var body = Body("<div data-kb='7'><p>a</p></div>");
        var before = body.OuterHtml;

        _blocks.GetBlocks(body);

        Assert.Equal(before, body.OuterHtml);
    }

    [Fact]
    public void SkippedTags_AreExactlyTheSetTheReaderSkips()
    {
        // The reader rebuilds each block's text with this same list, so changing it is a contract change
        Assert.Equal(
            ["iframe", "noembed", "noframes", "noscript", "plaintext", "rp", "rt", "script", "select", "style",
                "template", "textarea", "title", "xmp"],
            EpubTextBlocks.SkippedTags.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void RawTextAndDroppedContent_HoldNoBlocksAndNoText()
    {
        var blocks = _blocks.GetBlocks(Body(
            "<iframe><p>framed</p></iframe><noembed><p>e</p></noembed><noframes><p>f</p></noframes>" +
            "<xmp><p>x</p></xmp><select><option>o</option><div>d</div></select>" +
            "<p>a<textarea>t</textarea><title>t</title><iframe>i</iframe>b</p><plaintext><p>rest</p></plaintext>"));

        Assert.Equal(["ab"], blocks.Select(b => b.Text));
    }

    [Fact]
    public void ObjectFallback_IsOrdinaryContent()
    {
        Assert.Equal(["fallback"], Texts("<object data='x.svg'><p>fallback</p></object>"));
    }

    [Fact]
    public void StampBlocks_RemovesBookStampsInsideSkippedElements()
    {
        var body = Body(
            "<p>a<noscript><span data-kb='0'>n</span></noscript></p><template><p data-kb='1'>t</p></template>");

        _blocks.StampBlocks(body);

        Assert.Single(body.SelectNodes("//*[@data-kb]")!);
    }

    [Fact]
    public void BodyOutsideHtml_IsNotABlock()
    {
        var doc = new HtmlDocument();
        doc.LoadHtml("<body>loose text</body>");

        Assert.Empty(_blocks.GetBlocks(doc.DocumentNode.SelectSingleNode("//body")));
    }

    [Fact]
    public void LangAndRang_UseTheirHtml5CodePoints()
    {
        Assert.Equal(["⟨x⟩"], Texts("<p>&lang;x&rang;</p>"));
    }

    [Fact]
    public void DeepNesting_DoesNotOverflowTheStack()
    {
        const int depth = 20_000;
        var html = "<p>" + string.Concat(Enumerable.Repeat("<span>", depth)) + "deep" +
                   string.Concat(Enumerable.Repeat("</span>", depth)) + "</p><div>" +
                   string.Concat(Enumerable.Repeat("<div>", depth)) + "inner" +
                   string.Concat(Enumerable.Repeat("</div>", depth)) + "</div>";

        var found = _blocks.GetBlocks(Body(html));
        var stamped = _blocks.StampBlocks(Body(html));

        Assert.Equal(["deep", "inner"], found.Select(b => b.Text));
        Assert.Equal(found.Select(b => (b.Index, b.Node.Name, b.Text)),
            stamped.Select(b => (b.Index, b.Node.Name, b.Text)));
    }
}
