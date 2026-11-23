using System;
using System.Buffers;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using HtmlAgilityPack;
using Kavita.API.Services;

namespace Kavita.Services.Dictionary;

/// <summary>
/// Contract D, as <see cref="IEpubTextBlocks"/> documents it. Text sitting directly inside an element that also has
/// block children (the "intro" in <c>&lt;div&gt;intro&lt;p&gt;x&lt;/p&gt;&lt;/div&gt;</c>) belongs to no block, so
/// it's never tokenized.
/// </summary>
/// <remarks>
/// <para>
/// A block's text is what the browser holds after it parses the HTML that <c>GetBookPage</c> sends. That's why the
/// walk turns CR LF and lone CR into LF, drops NUL characters and the newline right after a <c>pre</c> or
/// <c>listing</c> start tag, and decodes numeric references the way the HTML spec says. The HTML parser does all of
/// that before any script sees the text. Every other whitespace character is kept.
/// </para>
/// <para>
/// Some markup still parses differently in the browser. A <c>p</c> that a <c>details</c>, <c>dialog</c>,
/// <c>hgroup</c> or <c>hr</c> start tag closes in the browser can keep that content on the server, even when the
/// tag sits inside a <c>span</c>. Named references without their semicolon, or outside the HTML 4 set, decode only
/// in the browser. A CDATA section inside <c>svg</c> or <c>math</c> is text in the browser and a comment here. In
/// each case the block's text comes out a different length on the two sides, and the reader drops that block when
/// its length check fails.
/// </para>
/// </remarks>
public class EpubTextBlocks : IEpubTextBlocks
{
    /// <summary>
    /// The tags that can be a block. An element with one of these tags is a block when no descendant has one.
    /// <c>body</c> is here so a page whose text sits straight in the body still gets a block, but only under an
    /// <c>html</c> element, because otherwise the body's own tag never reaches the reader to carry the stamp
    /// </summary>
    public static readonly FrozenSet<string> BlockTags = FrozenSet.ToFrozenSet(
    [
        "body", "p", "div", "li", "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "dd", "dt", "td", "th",
        "figure", "figcaption", "pre", "caption", "summary", "address", "section", "article", "aside", "header",
        "footer", "main", "nav", "legend", "center",
    ], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The tags whose text is left out of a block's text, and which never hold a block
    /// </summary>
    /// <remarks>
    /// <c>rt</c> and <c>rp</c> hold furigana and its fallback parentheses, which aren't part of the sentence. The
    /// browser parses the content of <c>iframe</c>, <c>noembed</c>, <c>noframes</c>, <c>xmp</c>, <c>plaintext</c>,
    /// <c>textarea</c>, <c>title</c>, and <c>noscript</c> (when scripts run) as plain text, keeps a
    /// <c>template</c>'s content out of the DOM, and drops or keeps tags inside a <c>select</c> depending on the
    /// browser. None of those can hold an element the reader could find. An <c>object</c>'s fallback content is
    /// ordinary DOM in both parsers, so it isn't here
    /// </remarks>
    public static readonly FrozenSet<string> SkippedTags = FrozenSet.ToFrozenSet(
    [
        "rt", "rp", "script", "style", "noscript", "template", "iframe", "noembed", "noframes", "xmp", "plaintext",
        "textarea", "title", "select",
    ], StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<EpubTextBlock> GetBlocks(HtmlNode body) => Walk(body, false);

    public IReadOnlyList<EpubTextBlock> StampBlocks(HtmlNode body) => Walk(body, true);

    /// <summary>
    /// An element the walk has entered and not yet left
    /// </summary>
    private sealed class OpenElement(HtmlNode node)
    {
        public HtmlNode Node { get; } = node;
        public int NextChild { get; set; }
        public bool HoldsBlock { get; set; }
    }

    /// <remarks>
    /// The walk keeps its own stack, because a broken or hostile book can nest elements deeper than the thread's
    /// stack allows for recursion
    /// </remarks>
    private static List<EpubTextBlock> Walk(HtmlNode body, bool stamp)
    {
        var blocks = new List<EpubTextBlock>();
        if (body.NodeType != HtmlNodeType.Element) return blocks;

        var open = new Stack<OpenElement>();
        open.Push(new OpenElement(body));
        while (open.Count > 0)
        {
            var element = open.Peek();
            if (element.NextChild < element.Node.ChildNodes.Count)
            {
                var child = element.Node.ChildNodes[element.NextChild++];
                if (child.NodeType != HtmlNodeType.Element) continue;

                if (SkippedTags.Contains(child.Name))
                {
                    if (stamp) RemoveStamps(child);
                    continue;
                }

                open.Push(new OpenElement(child));
                continue;
            }

            open.Pop();
            if (Leave(element, blocks, stamp) && open.Count > 0) open.Peek().HoldsBlock = true;
        }

        return blocks;
    }

    /// <returns>Whether the element is a block or holds one</returns>
    private static bool Leave(OpenElement element, List<EpubTextBlock> blocks, bool stamp)
    {
        var node = element.Node;
        if (element.HoldsBlock || !CanBeBlock(node))
        {
            // A data-kb the book itself carries would point the reader at a block that doesn't exist
            if (stamp && node.HasAttributes) node.Attributes.Remove(IEpubTextBlocks.BlockAttribute);
            return element.HoldsBlock;
        }

        // Blocks never nest, so a block found as the walk leaves it still comes out in document order
        var block = new EpubTextBlock(blocks.Count, node, CollectText(node));
        blocks.Add(block);
        if (stamp)
        {
            node.SetAttributeValue(IEpubTextBlocks.BlockAttribute, block.Index.ToString(CultureInfo.InvariantCulture));
        }

        return true;
    }

    private static bool CanBeBlock(HtmlNode node)
    {
        if (!BlockTags.Contains(node.Name)) return false;
        if (!node.Name.Equals("body", StringComparison.OrdinalIgnoreCase)) return true;

        for (var parent = node.ParentNode; parent != null; parent = parent.ParentNode)
        {
            if (parent.Name.Equals("html", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static void RemoveStamps(HtmlNode root)
    {
        var pending = new Stack<HtmlNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node.HasAttributes) node.Attributes.Remove(IEpubTextBlocks.BlockAttribute);
            foreach (var child in node.ChildNodes)
            {
                if (child.NodeType == HtmlNodeType.Element) pending.Push(child);
            }
        }
    }

    private static string CollectText(HtmlNode block)
    {
        var text = new StringBuilder();
        var open = new Stack<OpenElement>();
        open.Push(new OpenElement(block));
        while (open.Count > 0)
        {
            var element = open.Peek();
            if (element.NextChild >= element.Node.ChildNodes.Count)
            {
                open.Pop();
                continue;
            }

            switch (element.Node.ChildNodes[element.NextChild++])
            {
                case HtmlTextNode textNode:
                    text.Append(DecodeAsBrowser(textNode));
                    break;
                case { NodeType: HtmlNodeType.Element } child when !SkippedTags.Contains(child.Name):
                    open.Push(new OpenElement(child));
                    break;
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// The text the browser builds from a raw text node of the served HTML
    /// </summary>
    private static string DecodeAsBrowser(HtmlTextNode textNode)
    {
        var raw = textNode.Text;
        if (raw.Contains('\r')) raw = raw.Replace("\r\n", "\n").Replace('\r', '\n');
        // The parser drops a NUL in body text. A &#0; reference still becomes U+FFFD further down
        if (raw.Contains('\0')) raw = raw.Replace("\0", string.Empty);

        if (raw.StartsWith('\n') && textNode.PreviousSibling == null && textNode.ParentNode != null &&
            LeadingNewlineTags.Contains(textNode.ParentNode.Name))
        {
            raw = raw[1..];
        }

        return raw.Contains('&') ? DecodeReferences(raw) : raw;
    }

    private static readonly FrozenSet<string> LeadingNewlineTags = FrozenSet.ToFrozenSet(
        ["pre", "listing"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What the HTML parser makes of the character references in <paramref name="raw"/>
    /// </summary>
    /// <remarks>
    /// Numeric references follow the HTML spec in full, so <c>&amp;#151;</c> from a book converted out of
    /// Windows-1252 becomes an em dash as it does in the browser. Named references need their semicolon and are the
    /// HTML 4 set <see cref="WebUtility.HtmlDecode"/> knows, with <c>&amp;lang;</c> and <c>&amp;rang;</c> moved to the
    /// code points HTML 5 gives them. EPUB 3 allows only the five XML names and EPUB 2 the HTML 4 ones, so the names
    /// this leaves undecoded need invalid XHTML
    /// </remarks>
    private static string DecodeReferences(string raw)
    {
        var text = new StringBuilder(raw.Length);
        var i = 0;
        while (i < raw.Length)
        {
            var amp = raw.IndexOf('&', i);
            if (amp < 0)
            {
                text.Append(raw, i, raw.Length - i);
                break;
            }

            text.Append(raw, i, amp - i);
            i = amp + (amp + 1 < raw.Length && raw[amp + 1] == '#'
                ? AppendNumericReference(raw, amp, text)
                : AppendNamedReference(raw, amp, text));
        }

        return text.ToString();
    }

    /// <returns>How many characters of <paramref name="raw"/> the reference used</returns>
    private static int AppendNamedReference(string raw, int amp, StringBuilder text)
    {
        var semicolon = raw.IndexOf(';', amp + 1);
        if (semicolon > amp + 1 && semicolon - amp + 1 <= MaxNamedReferenceLength &&
            !raw.AsSpan(amp + 1, semicolon - amp - 1).ContainsAnyExcept(AsciiLettersAndDigits))
        {
            var reference = raw[amp..(semicolon + 1)];
            var decoded = reference switch
            {
                "&lang;" => "⟨",
                "&rang;" => "⟩",
                _ => WebUtility.HtmlDecode(reference),
            };
            if (!string.Equals(decoded, reference, StringComparison.Ordinal))
            {
                text.Append(decoded);
                return reference.Length;
            }
        }

        text.Append('&');
        return 1;
    }

    /// <summary>
    /// The longest HTML 4 name, <c>thetasym</c>, plus the ampersand and the semicolon
    /// </summary>
    private const int MaxNamedReferenceLength = 10;

    private static readonly SearchValues<char> AsciiLettersAndDigits =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789");

    /// <returns>How many characters of <paramref name="raw"/> the reference used</returns>
    private static int AppendNumericReference(string raw, int amp, StringBuilder text)
    {
        var start = amp + 2;
        var hex = start < raw.Length && raw[start] is 'x' or 'X';
        if (hex) start++;

        var end = start;
        var value = 0L;
        while (end < raw.Length && (hex ? char.IsAsciiHexDigit(raw[end]) : char.IsAsciiDigit(raw[end])))
        {
            // Past U+10FFFF the value only matters as "too big", so stop it growing
            value = Math.Min(value * (hex ? 16 : 10) + HexDigitValue(raw[end]), 0x110000);
            end++;
        }

        if (end == start)
        {
            // "&#" or "&#x" with no digits isn't a reference, and the browser keeps it as text
            text.Append(raw, amp, end - amp);
            return end - amp;
        }

        if (end < raw.Length && raw[end] == ';') end++;
        text.Append(NumericReferenceText(value));
        return end - amp;
    }

    private static int HexDigitValue(char digit) => digit <= '9' ? digit - '0' : (digit | 0x20) - 'a' + 10;

    private static string NumericReferenceText(long value)
    {
        if (value == 0 || value > 0x10FFFF || value is >= 0xD800 and <= 0xDFFF) return "�";

        if (value is >= 0x80 and <= 0x9F && Windows1252[(int) value - 0x80] != '\0')
        {
            return Windows1252[(int) value - 0x80].ToString();
        }

        return char.ConvertFromUtf32((int) value);
    }

    /// <summary>
    /// The HTML spec's table for references to U+0080 through U+009F. A NUL means the code point is kept as it is
    /// </summary>
    private static readonly char[] Windows1252 =
    [
        '€', '\0', '‚', 'ƒ', '„', '…', '†', '‡',
        'ˆ', '‰', 'Š', '‹', 'Œ', '\0', 'Ž', '\0',
        '\0', '‘', '’', '“', '”', '•', '–', '—',
        '˜', '™', 'š', '›', 'œ', '\0', 'ž', 'Ÿ',
    ];
}
