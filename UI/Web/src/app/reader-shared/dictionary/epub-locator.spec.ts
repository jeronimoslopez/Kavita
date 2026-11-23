import {EpubPageTokens} from '../../_models/dictionary/dictionary';
import {CaretDom} from './caret';
import {locateEpubWord} from './epub-locator';

// Block 0 reads 今日は新聞社へ, and block 1 is a link
const HTML = '<p data-kb="0">今日<ruby>は<rt>わ</rt></ruby><app-epub-highlight>新聞</app-epub-highlight>社へ</p>'
  + '<p data-kb="1"><a href="#n1">注</a></p>'
  + '<p data-kb="2"><span kavita-part="x">部</span></p>'
  + '<p data-kb="3">長さ違い</p>';

function page(): EpubPageTokens {
  return {
    version: 'v1',
    blocks: [
      {index: 0, length: 7, tokens: [
        [0, 2, 0, 'きょう', null],
        [2, 3, null, null, null],
        [3, 6, 1, 'しんぶんしゃ', [[3, 5, 2, 'しんぶん'], [5, 6, 3, 'しゃ']]],
        [6, 7, null, null, null],
      ]},
      {index: 1, length: 1, tokens: [[0, 1, 4, null, null]]},
      {index: 2, length: 1, tokens: [[0, 1, 5, null, null]]},
      {index: 3, length: 9, tokens: [[0, 4, 6, null, null]]},
    ],
    words: {},
    analysis: [],
  };
}

function setUp() {
  const container = document.createElement('div');
  container.innerHTML = HTML;
  document.body.appendChild(container);
  return container;
}

function text(container: Element, data: string): Text {
  const walker = document.createTreeWalker(container, NodeFilter.SHOW_TEXT);
  for (let node = walker.nextNode(); node !== null; node = walker.nextNode()) {
    if ((node as Text).data === data) return node as Text;
  }
  throw new Error(`no text node "${data}"`);
}

/** A caret at a fixed position whose character box always holds the point */
function caretOn(node: Node, offset: number): CaretDom {
  return {
    caretAt: () => ({node, offset}),
    rectsOf: () => [{left: 0, right: 100, top: 0, bottom: 100} as DOMRectReadOnly],
  };
}

describe('locateEpubWord', () => {
  it('returns the token under the point with a range over its text', () => {
    const container = setUp();
    const hit = locateEpubWord(1, 1, container, page(), {dom: caretOn(text(container, '社へ'), 0)})!;
    expect(hit.blockIndex).toBe(0);
    expect([hit.s, hit.e, hit.offset]).toEqual([3, 6, 5]);
    expect(hit.token[2]).toBe(1);
    expect(hit.range.toString()).toBe('新聞社');
  });

  it('looks words up through a highlight wrapper', () => {
    const container = setUp();
    const hit = locateEpubWord(1, 1, container, page(), {dom: caretOn(text(container, '新聞'), 1)});
    expect(hit?.s).toBe(3);
  });

  it('returns null for a token with no word and for ruby readings', () => {
    const container = setUp();
    expect(locateEpubWord(1, 1, container, page(), {dom: caretOn(text(container, 'は'), 0)})).toBeNull();
    expect(locateEpubWord(1, 1, container, page(), {dom: caretOn(text(container, 'わ'), 0)})).toBeNull();
  });

  it('returns null inside links and kavita-part anchors', () => {
    const container = setUp();
    expect(locateEpubWord(1, 1, container, page(), {dom: caretOn(text(container, '注'), 0)})).toBeNull();
    expect(locateEpubWord(1, 1, container, page(), {dom: caretOn(text(container, '部'), 0)})).toBeNull();
  });

  it('returns null and warns once for a block whose length check fails', () => {
    const container = setUp();
    const warn = vi.fn();
    const payload = page();
    const dom = caretOn(text(container, '長さ違い'), 0);
    expect(locateEpubWord(1, 1, container, payload, {dom, warn})).toBeNull();
    expect(locateEpubWord(1, 1, container, payload, {dom, warn})).toBeNull();
    expect(warn).toHaveBeenCalledTimes(1);
    expect(warn.mock.calls[0][0]).toContain('block 3');
  });

  it('returns null for a block the payload lacks and for text outside the container', () => {
    const container = setUp();
    const payload = page();
    payload.blocks = payload.blocks.filter(block => block.index !== 0);
    expect(locateEpubWord(1, 1, container, payload, {dom: caretOn(text(container, '今日'), 0)})).toBeNull();

    const outside = document.createElement('p');
    outside.setAttribute('data-kb', '0');
    outside.textContent = '今日は新聞社へ';
    document.body.appendChild(outside);
    expect(locateEpubWord(1, 1, container, page(), {dom: caretOn(outside.firstChild!, 0)})).toBeNull();
  });

  it('returns null when the caret hit test misses', () => {
    const container = setUp();
    const dom: CaretDom = {caretAt: () => ({node: text(container, '今日'), offset: 0}), rectsOf: () => []};
    expect(locateEpubWord(1, 1, container, page(), {dom})).toBeNull();
  });
});
