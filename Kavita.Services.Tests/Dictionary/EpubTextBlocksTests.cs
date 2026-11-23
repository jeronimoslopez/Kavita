using HtmlAgilityPack;
using Kavita.API.Services;
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
        Assert.Equal(["line1\n  line2\n", "\nkept", "\n\nfirst dropped", "ta!"], Texts(
            "<pre>\nline1\n  line2\n</pre>" +
            "<pre><code>\nkept</code></pre>" +
            "<pre>\n\n\nfirst dropped</pre>" +
            "<p><textarea>\nta</textarea>!</p>"));
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
}
