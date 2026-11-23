using System.Collections.Generic;
using HtmlAgilityPack;

namespace Kavita.API.Services;

/// <summary>
/// Contract D, the one definition of an EPUB page's text blocks. The book page pipeline stamps blocks with it and the
/// tokenizer reads them with it, and both calls make the same walk, so a block's index means the same thing to both.
/// </summary>
/// <remarks>
/// A block is an element with a block-level tag and no block-level descendant. Its text is its descendant text nodes in
/// document order, entity-decoded, skipping <c>rt</c>, <c>rp</c>, <c>script</c> and <c>style</c>, with whitespace left
/// as it is. Blocks are numbered from 0 in document order. Blocks whose text is empty keep their number, so indices
/// don't depend on the text.
/// </remarks>
public interface IEpubTextBlocks
{
    /// <summary>
    /// The attribute <see cref="StampBlocks"/> writes, holding the block's index
    /// </summary>
    const string BlockAttribute = "data-kb";

    /// <summary>
    /// Finds the blocks under <paramref name="body"/> without changing the document
    /// </summary>
    /// <param name="body">The page's <c>body</c> element, parsed from the spine item's content before any other rewrite</param>
    /// <returns>Every block in document order. A block's <see cref="EpubTextBlock.Index"/> is its position in this list</returns>
    IReadOnlyList<EpubTextBlock> GetBlocks(HtmlNode body);
    /// <summary>
    /// Sets <see cref="BlockAttribute"/> on each block <see cref="GetBlocks"/> finds, replacing any value already there.
    /// It adds attributes only and never adds or moves an element, because annotations and reading progress anchor by
    /// positional XPath
    /// </summary>
    /// <returns>The blocks it stamped, the same list <see cref="GetBlocks"/> would return</returns>
    IReadOnlyList<EpubTextBlock> StampBlocks(HtmlNode body);
}

/// <param name="Index">The block's position in document order, which is also its <c>data-kb</c> value</param>
/// <param name="Node">The block element</param>
/// <param name="Text">The block's text. Token offsets are UTF-16 offsets into it</param>
public sealed record EpubTextBlock(int Index, HtmlNode Node, string Text);
