using System.IO.Abstractions;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Kavita.API.Database;
using Kavita.API.Services;
using Kavita.Common;
using Kavita.Common.Helpers;
using Kavita.Models.DTOs.Account;
using Kavita.Models.DTOs.Dictionary;
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
    private static readonly string VerticalCss = """
        html { writing-mode: vertical-rl; -epub-writing-mode: vertical-rl; -webkit-writing-mode: vertical-rl; }
        .tcy { text-combine-upright: all; }
        """.ReplaceLineEndings("\n");

    /// <summary>
    /// A first chapter in the shape most Japanese EPUBs take, with entities that EPUB 2 books still carry
    /// </summary>
    private static readonly string JapanesePage = """
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
        """.ReplaceLineEndings("\n");

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
        """.ReplaceLineEndings("\r\n");

    private static readonly string StructuresPage = """
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
        <p>Shown<noscript>hidden</noscript><template><b>inert</b></template><iframe>raw</iframe> text</p>
        <noscript><p>Hidden paragraph</p></noscript>
        <template><p>Inert paragraph</p></template>
        <h3>The end</h3>
        </body>
        </html>
        """.ReplaceLineEndings("\n");

    private static readonly string BodyOnlyPage = """
        <?xml version="1.0" encoding="UTF-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml">
        <head><title>Body only</title></head>
        <body>本文だけの頁。<br/>二行目は<em>ここ</em>。</body>
        </html>
        """.ReplaceLineEndings("\n");

    private readonly BookService _bookService = CreateBookService(new EpubTextBlocks());

    private static BookService CreateBookService(IEpubTextBlocks epubTextBlocks)
    {
        var directoryService = new DirectoryService(Substitute.For<ILogger<DirectoryService>>(), new FileSystem());
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.UserRepository.GetAuthKeysForUserId(1, Arg.Any<CancellationToken>()).Returns(
            [new AuthKeyDto { Key = "key", Name = AuthKeyHelper.ImageOnlyKeyName, Provider = AuthKeyProvider.System }]);

        return new BookService(Substitute.For<ILogger<BookService>>(), directoryService,
            new ImageService(Substitute.For<ILogger<ImageService>>(), directoryService),
            Substitute.For<IMediaErrorService>(), unitOfWork, epubTextBlocks);
    }

    /// <summary>
    /// Finds and stamps nothing, which is how BookService served pages before blocks existed
    /// </summary>
    private sealed class NoBlocks : IEpubTextBlocks
    {
        public IReadOnlyList<EpubTextBlock> GetBlocks(HtmlNode body) => [];
        public IReadOnlyList<EpubTextBlock> StampBlocks(HtmlNode body) => [];
    }

    private static List<AnnotationDto> JapaneseAnnotations() =>
    [
        Annotation(1, "//body/div[1]/p[1]", "//body/div[1]/p[1]", "は猫である"),
        Annotation(2, "//BODY/DIV[1]/DIV[1]/P[1]", "//BODY/DIV[1]/DIV[1]/P[2]", "名前はまだ無い。\nどこで"),
        Annotation(3, """id("p3")""", """id("p3")""", "記号"),
    ];

    private static List<PersonalToCDto> Bookmarks(string scopedXPath) =>
    [
        new() { Id = 5, ChapterId = 1, PageNumber = 0, Title = "Mark", BookScrollId = BookService.BookReaderBodyScope + scopedXPath },
    ];

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
    public async Task JapaneseBook_BlocksKeepTheFuriganaBesideTheText()
    {
        using var book = JapaneseBook();

        var first = (await _bookService.GetEpubPageBlocksAsync(book.FilePath, 0))!;
        var second = (await _bookService.GetEpubPageBlocksAsync(book.FilePath, 1))!;

        Assert.Equal(
        [
            [new RubyDto(4, 5, "はじ")],
            [new RubyDto(1, 3, "わがはい")],
            [new RubyDto(3, 4, "うま")],
            [], [], [], [],
        ], first.Select(b => b.Ruby));
        Assert.Equal(
        [
            [],
            [new RubyDto(0, 2, "とうきょう"), new RubyDto(3, 5, "おおさか"), new RubyDto(10, 11, "と", 0), new RubyDto(11, 12, "し", 0)],
            [], [], [],
        ], second.Select(b => b.Ruby));
        // Each span's base is the text the book prints the reading over
        Assert.Equal(["始", "吾輩", "生"], first.SelectMany(b => b.Ruby.Select(r => b.Text[r.S..r.E])));
        Assert.Equal(["東京", "大阪", "都", "市"], second.SelectMany(b => b.Ruby.Select(r => b.Text[r.S..r.E])));
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
            "Term", "Definition", "code line 1\n  code line 2\n", "Before after.", "", "Shown text", "The end",
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

        var served = await AssertServedStampsMatchBlocks(book.FilePath, 0, JapaneseAnnotations(),
            Bookmarks("/DIV[1]/P[2]"));

        // Each annotation still resolves to the block it was made in
        Assert.Equal(["1"], HighlightBlocks(served, 1));
        Assert.Equal(["1", "2"], HighlightBlocks(served, 2));
        Assert.Equal(["4"], HighlightBlocks(served, 3));
        var bookmark = served.GetElementbyId("ptoc-5");
        Assert.NotNull(bookmark);
        Assert.Equal("2", bookmark.ParentNode.GetAttributeValue(IEpubTextBlocks.BlockAttribute, ""));
    }

    public static TheoryData<bool, int> EveryFixturePage => new()
    {
        { true, 0 }, { true, 1 }, { false, 0 }, { false, 1 },
    };

    /// <summary>
    /// The annotations and bookmark each fixture page is served with, one of each per block-holding structure
    /// </summary>
    private static (List<AnnotationDto> Annotations, List<PersonalToCDto> Bookmarks) Decorations(bool japanese, int page)
    {
        return (japanese, page) switch
        {
            (true, 0) => (JapaneseAnnotations(), Bookmarks("/DIV[1]/P[2]")),
            (true, 1) => ([Annotation(1, "//body/section[1]/h2[1]", "//body/section[1]/h2[1]", "第二章")],
                Bookmarks("/SECTION[1]/BLOCKQUOTE[1]/P[1]")),
            (false, 0) => ([Annotation(1, "//body/div[1]/p[1]", "//body/div[1]/p[1]", "paragraph")],
                Bookmarks("/H3[1]")),
            _ => ([Annotation(1, "//body/em[1]", "//body/em[1]", "ここ")], Bookmarks("/EM[1]")),
        };
    }

    [Theory]
    [MemberData(nameof(EveryFixturePage))]
    public async Task ServedPage_DiffersFromAnUnstampedPageOnlyByTheStamps(bool japanese, int page)
    {
        using var book = japanese ? JapaneseBook() : StructuresBook();
        var (annotations, bookmarks) = Decorations(japanese, page);

        var stamped = await _bookService.GetBookPage(1, page, 1, book.FilePath, book.FilePath, "//localhost/api/",
            bookmarks, annotations);
        var unstamped = await CreateBookService(new NoBlocks()).GetBookPage(1, page, 1, book.FilePath, book.FilePath,
            "//localhost/api/", bookmarks, annotations);

        Assert.Contains("epub-highlight-1", stamped);
        Assert.Contains("ptoc-5", stamped);
        Assert.Contains(" data-kb=\"0\"", stamped);
        Assert.Equal(unstamped, Regex.Replace(stamped, " data-kb=\"[0-9]+\"", string.Empty));
    }

    [Theory]
    [MemberData(nameof(EveryFixturePage))]
    public async Task ServedPage_WithAnnotationsAndBookmarks_KeepsStampsAndBlockText(bool japanese, int page)
    {
        using var book = japanese ? JapaneseBook() : StructuresBook();
        var (annotations, bookmarks) = Decorations(japanese, page);

        var served = await AssertServedStampsMatchBlocks(book.FilePath, page, annotations, bookmarks);

        Assert.NotNull(served.GetElementbyId("ptoc-5"));
        Assert.NotNull(served.GetElementbyId("epub-highlight-1"));
    }

    [Fact]
    public async Task EmptyPage_HasNoBlocks_AndGetBookPageStillReportsIt()
    {
        using var book = new EpubFixture("ja", VerticalCss, "", JapanesePage);

        Assert.Null(await _bookService.GetEpubPageBlocksAsync(book.FilePath, 0));
        var error = await Assert.ThrowsAsync<KavitaException>(() =>
            _bookService.GetBookPage(1, 0, 1, book.FilePath, book.FilePath, "//localhost/api/", [], []));
        Assert.Equal("epub-html-missing", error.Message);
        Assert.NotNull(await _bookService.GetEpubPageBlocksAsync(book.FilePath, 1));
    }

    [Fact]
    public void BrowserText_SkipsWhatTheWalkSkips()
    {
        Assert.Equal(BrowserSkippedTags.Order(StringComparer.Ordinal), EpubTextBlocks.SkippedTags.Order(StringComparer.Ordinal));
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
    /// The list the reader is told to skip, written out on its own so a change to the walk's list shows up here
    /// </summary>
    private static readonly string[] BrowserSkippedTags =
    [
        "rt", "rp", "script", "style", "noscript", "template", "iframe", "noembed", "noframes", "xmp", "plaintext",
        "textarea", "title", "select",
    ];

    /// <summary>
    /// What the reader collects from a stamped element in the live DOM. It reads every text node under the element in
    /// document order, through any highlight or bookmark wrapper, and skips what the contract skips
    /// </summary>
    private static string BrowserText(HtmlNode element)
    {
        var text = new StringBuilder();
        foreach (var node in element.Descendants().OfType<HtmlTextNode>())
        {
            if (node.Ancestors().TakeWhile(a => a != element).Any(a => BrowserSkippedTags.Contains(a.Name))) continue;

            // The browser's parser does these before any script can read the text
            var raw = node.Text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\0", string.Empty);
            if (node.ParentNode.Name == "pre" && node.PreviousSibling == null && raw.StartsWith('\n')) raw = raw[1..];
            text.Append(WebUtility.HtmlDecode(raw));
        }

        return text.ToString();
    }
}
