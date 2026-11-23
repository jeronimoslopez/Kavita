using System.IO.Abstractions;
using System.Net;
using System.Text;
using HtmlAgilityPack;
using Kavita.API.Database;
using Kavita.API.Services;
using Kavita.Common.Helpers;
using Kavita.Models.DTOs.Account;
using Kavita.Models.DTOs.Reader;
using Kavita.Models.Entities.Enums.User;
using Kavita.Services.Dictionary;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Kavita.Services.Tests.Dictionary;

/// <summary>
/// The <c>data-kb</c> stamps in what <see cref="BookService.GetBookPage"/> serves have to line up with the blocks
/// <see cref="BookService.GetEpubPageBlocksAsync"/> hands the tokenizer, or every token offset lands in the wrong place
/// </summary>
public class EpubPageBlocksTests
{
    private const string VerticalCss = """
        html { writing-mode: vertical-rl; -epub-writing-mode: vertical-rl; -webkit-writing-mode: vertical-rl; }
        .tcy { text-combine-upright: all; }
        """;

    /// <summary>
    /// A first chapter in the shape most Japanese EPUBs take, with entities that EPUB 2 books still carry
    /// </summary>
    private const string JapanesePage = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE html>
        <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" xml:lang="ja" class="vrtl">
        <head>
        <title>第一章</title>
        <link rel="stylesheet" type="text/css" href="style.css"/>
        <style>p { text-indent: 1em; }</style>
        </head>
        <body class="main">
        <div class="main_text">
        <h1 id="h1">第一章　<ruby>始<rp>（</rp><rt>はじ</rt><rp>）</rp></ruby>まり</h1>
        <p id="p1">　<ruby><rb>吾輩</rb><rt>わがはい</rt></ruby>は猫である。名前はまだ無い。</p>
        <p id="p2">どこで<ruby>生<rt>うま</rt></ruby>れたかとんと<span class="em">見当</span>がつかぬ。&#x1F431;</p>
        <p><br/></p>
        <p id="p3">「&amp;」と&nbsp;&lt;記号&gt;&#12354;。</p>
        <div class="pagebreak"/>
        <p id="p4"><a href="page1.xhtml#n1" epub:type="noteref">注1</a>を参照。平成<span class="tcy">31</span>年。</p>
        </div>
        </body>
        </html>
        """;

    private static readonly string JapaneseCrLfPage = """
        <?xml version="1.0" encoding="UTF-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" xml:lang="ja">
        <head><title>第二章</title><link rel="stylesheet" type="text/css" href="style.css"/></head>
        <body>
        <section epub:type="chapter">
        <h2>第二章</h2>
        <p><ruby>東京<rt>とうきょう</rt></ruby>と<ruby>大阪<rp>(</rp><rt>おおさか</rt><rp>)</rp></ruby>、
        二つの<ruby><rb>都</rb><rb>市</rb><rt>と</rt><rt>し</rt></ruby>。</p>
        <p class="image"><img src="cover.jpg" alt="表紙"/></p>
        <blockquote><p>引用された一文。</p></blockquote>
        </section>
        <aside id="n1" epub:type="footnote"><p>注釈の本文。</p></aside>
        </body>
        </html>
        """.Replace("\n", "\r\n");

    private const string StructuresPage = """
        <?xml version="1.0" encoding="UTF-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml">
        <head><title>Structures</title><script type="text/javascript" src="x.js"/></head>
        <body>
        <div>Loose intro <p>Inside a paragraph.</p> loose tail</div>
        <table>
          <caption>Prices</caption>
          <tr><th>Item</th><th>Cost</th></tr>
          <tr><td>Tea &amp; cake</td><td><p>£3</p><p>or €4</p></td></tr>
        </table>
        <ul><li>One</li><li>Two<ol><li>Two point one</li></ol></li></ul>
        <dl><dt>Term</dt><dd>Definition</dd></dl>
        <pre>
        code line 1
          code line 2
        </pre>
        <p>Before <script>var s = "</p>";</script>after.</p>
        <p/>
        <h3>The end</h3>
        </body>
        </html>
        """;

    private const string BodyOnlyPage = """
        <?xml version="1.0" encoding="UTF-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml">
        <head><title>Body only</title></head>
        <body>本文だけの頁。<br/>二行目は<em>ここ</em>。</body>
        </html>
        """;

    private readonly BookService _bookService;

    public EpubPageBlocksTests()
    {
        var directoryService = new DirectoryService(Substitute.For<ILogger<DirectoryService>>(), new FileSystem());
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.UserRepository.GetAuthKeysForUserId(1, Arg.Any<CancellationToken>()).Returns(
            [new AuthKeyDto { Key = "key", Name = AuthKeyHelper.ImageOnlyKeyName, Provider = AuthKeyProvider.System }]);

        _bookService = new BookService(Substitute.For<ILogger<BookService>>(), directoryService,
            new ImageService(Substitute.For<ILogger<ImageService>>(), directoryService),
            Substitute.For<IMediaErrorService>(), unitOfWork, new EpubTextBlocks());
    }

    private static EpubFixture JapaneseBook() => new("ja", VerticalCss, JapanesePage, JapaneseCrLfPage);

    private static EpubFixture StructuresBook() => new("en", "p { margin: 0; }", StructuresPage, BodyOnlyPage);

    [Fact]
    public async Task PageCount_IsTheSpineLength()
    {
        using var book = JapaneseBook();

        Assert.Equal(2, await _bookService.GetEpubPageCountAsync(book.FilePath));
    }

    [Fact]
    public async Task PageBlocks_AreNullOutsideTheSpine()
    {
        using var book = JapaneseBook();

        Assert.Null(await _bookService.GetEpubPageBlocksAsync(book.FilePath, 2));
        Assert.Null(await _bookService.GetEpubPageBlocksAsync(book.FilePath, -1));
    }

    [Fact]
    public async Task JapanesePage_BlocksHoldTheSentenceWithoutFurigana()
    {
        using var book = JapaneseBook();

        var blocks = await _bookService.GetEpubPageBlocksAsync(book.FilePath, 0);

        Assert.NotNull(blocks);
        Assert.Equal(
        [
            "第一章　始まり",
            "　吾輩は猫である。名前はまだ無い。",
            "どこで生れたかとんと見当がつかぬ。\U0001F431",
            "",
            "「&」と <記号>あ。",
            "",
            "注1を参照。平成31年。",
        ], blocks.Select(b => b.Text));
    }

    [Fact]
    public async Task CrLfPage_BlocksUseLf()
    {
        using var book = JapaneseBook();

        var blocks = await _bookService.GetEpubPageBlocksAsync(book.FilePath, 1);

        Assert.NotNull(blocks);
        Assert.Equal(["第二章", "東京と大阪、\n二つの都市。", "", "引用された一文。", "注釈の本文。"],
            blocks.Select(b => b.Text));
    }

    [Fact]
    public async Task StructuresPage_LooseTextGetsNoBlock()
    {
        using var book = StructuresBook();

        var blocks = await _bookService.GetEpubPageBlocksAsync(book.FilePath, 0);

        Assert.NotNull(blocks);
        Assert.Equal(
        [
            "Inside a paragraph.", "Prices", "Item", "Cost", "Tea & cake", "£3", "or €4", "One", "Two point one",
            "Term", "Definition", "code line 1\n  code line 2\n", "Before after.", "", "The end",
        ], blocks.Select(b => b.Text));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task JapaneseBook_ServedStampsMatchBlocks(int page)
    {
        using var book = JapaneseBook();

        await AssertServedStampsMatchBlocks(book.FilePath, page, [], []);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task StructuresBook_ServedStampsMatchBlocks(int page)
    {
        using var book = StructuresBook();

        await AssertServedStampsMatchBlocks(book.FilePath, page, [], []);
    }

    [Fact]
    public async Task BodyOnlyPage_StampReachesTheReaderOnTheWrapper()
    {
        using var book = StructuresBook();

        var html = await _bookService.GetBookPage(1, 1, 1, book.FilePath, book.FilePath, "//localhost/api/", [], []);

        var wrapper = Parse(html).DocumentNode.ChildNodes.Single(n => n.NodeType == HtmlNodeType.Element);
        Assert.Equal("div", wrapper.Name);
        Assert.Equal("0", wrapper.GetAttributeValue(IEpubTextBlocks.BlockAttribute, ""));
        Assert.Equal("本文だけの頁。二行目はここ。", BrowserText(wrapper));
    }

    [Fact]
    public async Task AnnotationsAndBookmarks_KeepStampsAndBlockText()
    {
        using var book = JapaneseBook();
        List<AnnotationDto> annotations =
        [
            Annotation(1, "//body/div[1]/p[1]", "//body/div[1]/p[1]", "は猫である"),
            Annotation(2, "//BODY/DIV[1]/DIV[1]/P[1]", "//BODY/DIV[1]/DIV[1]/P[2]", "名前はまだ無い。\nどこで"),
            Annotation(3, """id("p3")""", """id("p3")""", "記号"),
        ];
        List<PersonalToCDto> bookmarks =
        [
            new() { Id = 5, ChapterId = 1, PageNumber = 0, Title = "Mark", BookScrollId = BookService.BookReaderBodyScope + "/DIV[1]/P[2]" },
        ];

        var served = await AssertServedStampsMatchBlocks(book.FilePath, 0, annotations, bookmarks);

        // Each annotation still resolves to the block it was made in
        Assert.Equal(["1"], HighlightBlocks(served, 1));
        Assert.Equal(["1", "2"], HighlightBlocks(served, 2));
        Assert.Equal(["4"], HighlightBlocks(served, 3));
        var bookmark = served.GetElementbyId("ptoc-5");
        Assert.NotNull(bookmark);
        Assert.Equal("2", bookmark.ParentNode.GetAttributeValue(IEpubTextBlocks.BlockAttribute, ""));
    }

    private static List<string> HighlightBlocks(HtmlDocument served, int annotationId)
    {
        var highlights = served.DocumentNode.SelectNodes($"//app-epub-highlight[@data-annotation-id='{annotationId}']");
        Assert.NotNull(highlights);
        return highlights
            .Select(h => h.AncestorsAndSelf().First(a => a.Attributes.Contains(IEpubTextBlocks.BlockAttribute)))
            .Select(b => b.GetAttributeValue(IEpubTextBlocks.BlockAttribute, ""))
            .Distinct()
            .ToList();
    }

    private static AnnotationDto Annotation(int id, string xpath, string endingXPath, string selectedText) => new()
    {
        Id = id, XPath = xpath, EndingXPath = endingXPath, SelectedText = selectedText,
        ChapterId = 1, VolumeId = 1, SeriesId = 1, LibraryId = 1, OwnerUserId = 1,
    };

    /// <summary>
    /// Serves the page through the reader's path and checks it against the tokenizer's blocks
    /// </summary>
    /// <returns>The served page, parsed</returns>
    private async Task<HtmlDocument> AssertServedStampsMatchBlocks(string epubPath, int page,
        List<AnnotationDto> annotations, List<PersonalToCDto> bookmarks)
    {
        var blocks = await _bookService.GetEpubPageBlocksAsync(epubPath, page);
        var html = await _bookService.GetBookPage(1, page, 1, epubPath, epubPath, "//localhost/api/", bookmarks,
            annotations);

        Assert.NotNull(blocks);
        Assert.NotEmpty(blocks);
        var served = Parse(html);
        var stamped = served.DocumentNode.Descendants()
            .Where(n => n.NodeType == HtmlNodeType.Element && n.Attributes.Contains(IEpubTextBlocks.BlockAttribute))
            .ToList();

        Assert.Equal(blocks.Select(b => b.Index.ToString()),
            stamped.Select(n => n.GetAttributeValue(IEpubTextBlocks.BlockAttribute, "")));
        Assert.Equal(blocks.Select(b => b.Text), stamped.Select(BrowserText));
        return served;
    }

    private static HtmlDocument Parse(string html)
    {
        var doc = new HtmlDocument { OptionFixNestedTags = true };
        doc.LoadHtml(html);
        return doc;
    }

    /// <summary>
    /// What the reader collects from a stamped element in the live DOM. It reads every text node under the element in
    /// document order, through any highlight or bookmark wrapper, and skips furigana, script and style
    /// </summary>
    private static string BrowserText(HtmlNode element)
    {
        string[] skipped = ["rt", "rp", "script", "style"];
        var text = new StringBuilder();
        foreach (var node in element.Descendants().OfType<HtmlTextNode>())
        {
            if (node.Ancestors().TakeWhile(a => a != element).Any(a => skipped.Contains(a.Name))) continue;

            // The browser's parser does these two before any script can read the text
            var raw = node.Text.Replace("\r\n", "\n").Replace('\r', '\n');
            if (node.ParentNode.Name == "pre" && node.PreviousSibling == null && raw.StartsWith('\n')) raw = raw[1..];
            text.Append(WebUtility.HtmlDecode(raw));
        }

        return text.ToString();
    }
}
