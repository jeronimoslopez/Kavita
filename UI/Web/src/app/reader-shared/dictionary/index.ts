/** Locators and helpers for the reader dictionary. Nothing here imports Angular, so any reader can use it */

/** `locateEpubWord(x, y, container, page, options?)` takes client coordinates, the reading container and the page's EpubPageTokens, and returns an EpubHit or null */
export {locateEpubWord} from './epub-locator';
/** The hit locateEpubWord returns, with `blockIndex`, `token`, `s`, `e`, `offset` and `range` */
export type {EpubHit, EpubLocatorOptions} from './epub-locator';
/** The selector for elements whose clicks never become lookups, the ones the book reader treats as controls */
export {LOOKUP_EXCLUDED_SELECTOR} from './epub-locator';

/** `caretFromPoint(doc, x, y, dom?)` takes a document and client coordinates and returns the text node and offset of the character under the point, or null */
export {caretFromPoint, browserCaretDom} from './caret';
/** A text node and an offset in it, and the DOM calls caretFromPoint makes, which a spec can fake */
export type {TextPosition, CaretDom} from './caret';

/** `readBlockText(block)` returns a stamped block's text and its text nodes. `blockOffsetOf(text, node, offset)` returns a block offset or null. `rangeForBlockOffsets(text, s, e)` returns a Range or null */
export {readBlockText, blockOffsetOf, domPositionOf, rangeForBlockOffsets} from './block-text';
/** `enclosingBlock(node)` returns the nearest `[data-kb]` element or null, and `blockIndexOf(block)` its index or null */
export {enclosingBlock, blockIndexOf, BLOCK_ATTRIBUTE} from './block-text';
/** A block's text with the nodes it came from and where each starts */
export type {BlockText} from './block-text';

/** `tokenAt(tokens, offset)` and `subAt(token, offset)` return the token or compound part at a block offset, or null. `hasWord(token)` and `blockMatches(block, length)` return booleans */
export {tokenAt, subAt, hasWord, blockMatches} from './tokens';

/** `markOpenWord(range)` and `clearOpenWord()` set and clear the open word's highlight, and do nothing where the API is missing */
export {markOpenWord, clearOpenWord, supportsHighlights, OPEN_WORD_HIGHLIGHT} from './highlight';
/** The globals the highlight calls read, which a spec can fake */
export type {HighlightEnv} from './highlight';

/** `orderSenses(senses, analysis)` returns the senses with the analysis's choice first and flagged `chosen` */
export {orderSenses} from './senses';
/** A sense with its number in its own entry (`n`), the number to show (`label`) and whether the analysis chose it */
export type {OrderedSense} from './senses';

/** `toHiragana(s)` returns `s` with katakana as hiragana. `furigana_segments(surface, reading)` returns the runs to draw, each with its ruby or null */
export {toHiragana, furigana_segments} from './furigana';
/** One run of a surface and the ruby drawn over it, null for kana */
export type {FuriSegment} from './furigana';

/** `posLabel(code)` returns a short English label for a JMdict part-of-speech code, and `posLabels(codes)` the labels without repeats */
export {posLabel, posLabels} from './pos';
