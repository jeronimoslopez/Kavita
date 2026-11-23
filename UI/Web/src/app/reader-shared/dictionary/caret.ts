/** A position in a text node, as the caret APIs report it */
export interface TextPosition {
  node: Text;
  /** UTF-16 offset in the node's data */
  offset: number;
}

/** The DOM calls the hit test makes, kept separate because jsdom and happy-dom implement neither caret API */
export interface CaretDom {
  /** The caret position the browser reports for a point, which may be any node */
  caretAt(doc: Document, x: number, y: number): {node: Node, offset: number} | null;
  /** The client rects of the characters `[start, end)` of a text node */
  rectsOf(node: Text, start: number, end: number): ArrayLike<DOMRectReadOnly>;
}

export const browserCaretDom: CaretDom = {
  caretAt(doc, x, y) {
    if (typeof doc.caretPositionFromPoint === 'function') {
      const position = doc.caretPositionFromPoint(x, y);
      return position ? {node: position.offsetNode, offset: position.offset} : null;
    }
    // Older WebKit and Chromium builds only have the nonstandard call
    const legacy = doc as Document & {caretRangeFromPoint?: (x: number, y: number) => Range | null};
    if (typeof legacy.caretRangeFromPoint === 'function') {
      const range = legacy.caretRangeFromPoint(x, y);
      return range ? {node: range.startContainer, offset: range.startOffset} : null;
    }
    return null;
  },
  rectsOf(node, start, end) {
    const range = node.ownerDocument.createRange();
    range.setStart(node, start);
    range.setEnd(node, end);
    return range.getClientRects();
  },
};

function isHighSurrogate(code: number) {
  return code >= 0xd800 && code <= 0xdbff;
}

function isLowSurrogate(code: number) {
  return code >= 0xdc00 && code <= 0xdfff;
}

/** Widens `[start, end)` so it doesn't cut a surrogate pair in half */
function wholeCharacter(data: string, start: number, end: number): [number, number] {
  if (start > 0 && isLowSurrogate(data.charCodeAt(start)) && isHighSurrogate(data.charCodeAt(start - 1))) start--;
  if (end < data.length && isLowSurrogate(data.charCodeAt(end)) && isHighSurrogate(data.charCodeAt(end - 1))) end++;
  return [start, end];
}

function contains(rect: DOMRectReadOnly, x: number, y: number) {
  return x >= rect.left && x <= rect.right && y >= rect.top && y <= rect.bottom;
}

/**
 * The character under a point, as its text node and the offset where the character starts. Null when the point isn't
 * over text. The caret APIs snap to the nearest caret position even from the margin beside a line, so the point must
 * also fall inside the box of the character after the caret or the one before it
 */
export function caretFromPoint(doc: Document, x: number, y: number, dom: CaretDom = browserCaretDom): TextPosition | null {
  const caret = dom.caretAt(doc, x, y);
  if (caret === null || caret.node.nodeType !== Node.TEXT_NODE) return null;
  const node = caret.node as Text;
  const data = node.data;
  const offset = Math.max(0, Math.min(caret.offset, data.length));

  const candidates: [number, number][] = [];
  if (offset < data.length) candidates.push(wholeCharacter(data, offset, offset + 1));
  if (offset > 0) candidates.push(wholeCharacter(data, offset - 1, offset));
  for (const [start, end] of candidates) {
    const rects = dom.rectsOf(node, start, end);
    for (let i = 0; i < rects.length; i++) {
      if (contains(rects[i], x, y)) return {node, offset: start};
    }
  }
  return null;
}
