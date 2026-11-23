import {EpubPageBlock, EpubPageTokens, Token} from '../../_models/dictionary/dictionary';
import {blockIndexOf, blockOffsetOf, enclosingBlock, rangeForBlockOffsets, readBlockText} from './block-text';
import {browserCaretDom, caretFromPoint, CaretDom} from './caret';
import {blockMatches, hasWord, tokenAt} from './tokens';

/**
 * Clicks inside these go to the element, not the dictionary. It's the same attribute set the book reader's click
 * guard checks. Highlights aren't here, they're plain inline wrappers
 */
export const LOOKUP_EXCLUDED_SELECTOR = 'a[href], [kavita-part], [onclick], [role]';

export interface EpubHit {
  /** The block's `data-kb` value, sent as `block` in a lookup */
  blockIndex: number;
  token: Token;
  /** The token's start, sent as `s` in a lookup */
  s: number;
  e: number;
  /** Block offset of the character under the point, for picking a compound's part with subAt */
  offset: number;
  /**
   * The token's text in the DOM, for highlighting and placing the popup. When the token spans a ruby it also holds
   * the rt subtree, so take the surface from the block text and use the range only for geometry and highlighting
   */
  range: Range;
}

export interface EpubLocatorOptions {
  dom?: CaretDom;
  warn?: (message: string) => void;
}

/** Blocks already warned about, per page payload, so a mismatched block logs once and not on every click */
const warned = new WeakMap<EpubPageTokens, Set<number>>();
const blockMaps = new WeakMap<EpubPageTokens, Map<number, EpubPageBlock>>();

function pageBlock(page: EpubPageTokens, index: number): EpubPageBlock | undefined {
  let map = blockMaps.get(page);
  if (map === undefined) {
    map = new Map(page.blocks.map(block => [block.index, block]));
    blockMaps.set(page, map);
  }
  return map.get(index);
}

function warnOnce(page: EpubPageTokens, index: number, message: string, warn: (message: string) => void) {
  let seen = warned.get(page);
  if (seen === undefined) {
    seen = new Set();
    warned.set(page, seen);
  }
  if (seen.has(index)) return;
  seen.add(index);
  warn(message);
}

/** The word under a point in the reading container, or null when there's nothing there to look up */
export function locateEpubWord(x: number, y: number, container: Element, page: EpubPageTokens,
                               options: EpubLocatorOptions = {}): EpubHit | null {
  const caret = caretFromPoint(container.ownerDocument, x, y, options.dom ?? browserCaretDom);
  if (caret === null || !container.contains(caret.node)) return null;

  const excluded = caret.node.parentElement?.closest(LOOKUP_EXCLUDED_SELECTOR);
  if (excluded && container.contains(excluded)) return null;

  const element = enclosingBlock(caret.node);
  if (element === null || !container.contains(element)) return null;
  const blockIndex = blockIndexOf(element);
  if (blockIndex === null) return null;
  const block = pageBlock(page, blockIndex);
  if (block === undefined) return null;

  const text = readBlockText(element);
  if (!blockMatches(block, text.text.length)) {
    warnOnce(page, blockIndex,
      `Dictionary lookups are off for block ${blockIndex}: the page has ${text.text.length} characters and the server stored ${block.length}`,
      options.warn ?? (message => console.warn(message)));
    return null;
  }

  const offset = blockOffsetOf(text, caret.node, caret.offset);
  if (offset === null) return null;
  const token = tokenAt(block.tokens, offset);
  if (token === null || !hasWord(token)) return null;

  const range = rangeForBlockOffsets(text, token[0], token[1]);
  if (range === null) return null;
  return {blockIndex, token, s: token[0], e: token[1], offset, range};
}
