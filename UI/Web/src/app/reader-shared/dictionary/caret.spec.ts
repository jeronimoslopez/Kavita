import {browserCaretDom, caretFromPoint, CaretDom} from './caret';

/** Each UTF-16 unit is a 10 by 20 box laid out left to right from x = 0 */
function rect(left: number, right: number): DOMRectReadOnly {
  return {left, right, top: 0, bottom: 20, x: left, y: 0, width: right - left, height: 20} as DOMRectReadOnly;
}

function fakeDom(node: Node, offset: number): CaretDom & {calls: [number, number][]} {
  const calls: [number, number][] = [];
  return {
    calls,
    caretAt: () => ({node, offset}),
    rectsOf: (_node, start, end) => {
      calls.push([start, end]);
      return [rect(start * 10, end * 10)];
    },
  };
}

describe('caretFromPoint', () => {
  const node = document.createTextNode('abc');

  it('returns the character after the caret when the point is on it', () => {
    expect(caretFromPoint(document, 12, 5, fakeDom(node, 1))).toEqual({node, offset: 1});
  });

  it('returns the character before the caret when the point is on its right half', () => {
    expect(caretFromPoint(document, 18, 5, fakeDom(node, 2))).toEqual({node, offset: 1});
  });

  it('misses in the margin past the end of a line', () => {
    expect(caretFromPoint(document, 80, 5, fakeDom(node, 3))).toBeNull();
  });

  it('misses below the line even when the x lines up', () => {
    expect(caretFromPoint(document, 12, 40, fakeDom(node, 1))).toBeNull();
  });

  it('misses when the caret is not in a text node or there is no caret', () => {
    expect(caretFromPoint(document, 0, 0, fakeDom(document.createElement('p'), 0))).toBeNull();
    expect(caretFromPoint(document, 0, 0, {caretAt: () => null, rectsOf: () => []})).toBeNull();
  });

  it('measures a character outside the BMP as a whole pair', () => {
    const pair = document.createTextNode('a𠮟b');
    const dom = fakeDom(pair, 2);
    expect(caretFromPoint(document, 15, 5, dom)).toEqual({node: pair, offset: 1});
    expect(dom.calls[0]).toEqual([1, 3]);
  });
});

describe('browserCaretDom.caretAt', () => {
  const node = document.createTextNode('abc');

  it('uses caretPositionFromPoint where it exists', () => {
    const doc = {caretPositionFromPoint: () => ({offsetNode: node, offset: 2})} as unknown as Document;
    expect(browserCaretDom.caretAt(doc, 0, 0)).toEqual({node, offset: 2});
  });

  it('falls back to caretRangeFromPoint', () => {
    const doc = {caretRangeFromPoint: () => ({startContainer: node, startOffset: 1})} as unknown as Document;
    expect(browserCaretDom.caretAt(doc, 0, 0)).toEqual({node, offset: 1});
  });

  it('returns null when neither exists or the point hits nothing', () => {
    expect(browserCaretDom.caretAt({} as Document, 0, 0)).toBeNull();
    expect(browserCaretDom.caretAt({caretPositionFromPoint: () => null} as unknown as Document, 0, 0)).toBeNull();
  });
});
