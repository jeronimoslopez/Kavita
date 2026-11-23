import {
  blockIndexOf, blockOffsetOf, domPositionOf, enclosingBlock, rangeForBlockOffsets, readBlockText, SKIPPED_TEXT_TAGS,
} from './block-text';

function block(html: string): HTMLElement {
  const host = document.createElement('div');
  host.innerHTML = html;
  document.body.appendChild(host);
  return host.querySelector('[data-kb]') as HTMLElement;
}

function textNode(element: Element, data: string): Text {
  const walker = element.ownerDocument.createTreeWalker(element, NodeFilter.SHOW_TEXT);
  for (let node = walker.nextNode(); node !== null; node = walker.nextNode()) {
    if ((node as Text).data === data) return node as Text;
  }
  throw new Error(`no text node "${data}"`);
}

describe('readBlockText', () => {
  it('joins text across inline wrappers and leaves out rt, rp, script and style', () => {
    const el = block('<p data-kb="3">漢<ruby>字<rp>(</rp><rt>じ</rt><rp>)</rp></ruby>を'
      + '<app-epub-highlight>読<b>む</b></app-epub-highlight><script>x</script><style>p{}</style></p>');
    const text = readBlockText(el);
    expect(text.text).toBe('漢字を読む');
    expect(text.nodes.map(n => n.data)).toEqual(['漢', '字', 'を', '読', 'む']);
    expect(text.starts).toEqual([0, 1, 2, 3, 4]);
  });

  it('leaves out every element in the skip set', () => {
    // Built by hand and kept out of the document, because the HTML parser reshapes the tree around several of these
    // tags and jsdom would run the script
    const el = document.createElement('p');
    el.setAttribute('data-kb', '0');
    el.appendChild(document.createTextNode('a'));
    for (const tag of SKIPPED_TEXT_TAGS) {
      const skipped = document.createElement(tag);
      skipped.appendChild(document.createTextNode(tag));
      el.appendChild(skipped);
    }
    el.appendChild(document.createTextNode('b'));
    expect(readBlockText(el).text).toBe('ab');
  });

  it('compares tag names without regard to case', () => {
    const el = block('<p data-kb="0">a</p>');
    const rt = document.createElementNS('http://www.w3.org/1999/xhtml', 'RT');
    rt.textContent = 'x';
    el.appendChild(rt);
    expect(readBlockText(el).text).toBe('a');
  });

  it('skips exactly the tags the server skips', () => {
    expect([...SKIPPED_TEXT_TAGS].sort()).toEqual([
      'iframe', 'noembed', 'noframes', 'noscript', 'plaintext', 'rp', 'rt', 'script', 'select', 'style', 'template',
      'textarea', 'title', 'xmp',
    ]);
  });

  it('does not descend into a descendant stamped with its own data-kb', () => {
    const host = document.createElement('div');
    host.innerHTML = '<div data-kb="0">外<p data-kb="1">内</p>側</div>';
    const outer = host.firstElementChild!;
    const text = readBlockText(outer);
    expect(text.text).toBe('外側');
    expect(blockOffsetOf(text, textNode(outer, '内'), 0)).toBeNull();
    expect(readBlockText(host.querySelector('[data-kb="1"]')!).text).toBe('内');
  });

  it('keeps whitespace as it is', () => {
    expect(readBlockText(block('<p data-kb="0"> a\n <i>b </i></p>')).text).toBe(' a\n b ');
  });

  it('counts a character outside the BMP as two units, the same as the DOM offsets do', () => {
    const el = block('<p data-kb="0">𠮟<b>る</b></p>');
    const text = readBlockText(el);
    expect(text.text.length).toBe(3);
    expect(blockOffsetOf(text, textNode(el, 'る'), 0)).toBe(2);
    expect(rangeForBlockOffsets(text, 0, 2)?.toString()).toBe('𠮟');
    expect(domPositionOf(text, 2)).toEqual({node: textNode(el, 'る'), offset: 0});
  });
});

describe('blockOffsetOf', () => {
  it('converts a node and an offset in it to a block offset', () => {
    const el = block('<p data-kb="1">ab<span>cde</span>f</p>');
    const text = readBlockText(el);
    expect(blockOffsetOf(text, textNode(el, 'cde'), 2)).toBe(4);
    expect(blockOffsetOf(text, textNode(el, 'f'), 1)).toBe(6);
  });

  it('maps the offset at the end of a node to the start of the next one', () => {
    const el = block('<p data-kb="1">ab<span>cde</span></p>');
    const text = readBlockText(el);
    expect(blockOffsetOf(text, textNode(el, 'ab'), 2)).toBe(2);
    expect(blockOffsetOf(text, textNode(el, 'cde'), 3)).toBe(5);
  });

  it('skips an empty text node inside a block', () => {
    const el = block('<p data-kb="1">ab<span>cd</span></p>');
    const empty = document.createTextNode('');
    el.insertBefore(empty, el.lastChild);
    const text = readBlockText(el);
    expect(text.text).toBe('abcd');
    expect(text.nodes.map(n => n.data)).toEqual(['ab', 'cd']);
    expect(blockOffsetOf(text, empty, 0)).toBeNull();
    expect(rangeForBlockOffsets(text, 1, 3)?.toString()).toBe('bc');
  });

  it('returns null for a node the walk left out', () => {
    const el = block('<p data-kb="1"><ruby>字<rt>じ</rt></ruby></p>');
    expect(blockOffsetOf(readBlockText(el), textNode(el, 'じ'), 0)).toBeNull();
  });
});

describe('rangeForBlockOffsets', () => {
  it('spans several text nodes and wrappers', () => {
    const el = block('<p data-kb="0">漢<ruby>字<rt>じ</rt></ruby>を<app-epub-highlight>読む</app-epub-highlight></p>');
    const range = rangeForBlockOffsets(readBlockText(el), 1, 4)!;
    expect(range.startContainer).toBe(textNode(el, '字'));
    expect(range.startOffset).toBe(0);
    expect(range.endContainer).toBe(textNode(el, '読む'));
    expect(range.endOffset).toBe(1);
  });

  it('starts at the beginning of the later node and ends at the end of the earlier one on a boundary', () => {
    const el = block('<p data-kb="0">ab<i>cd</i>ef</p>');
    const range = rangeForBlockOffsets(readBlockText(el), 2, 4)!;
    expect(range.startContainer).toBe(textNode(el, 'cd'));
    expect(range.startOffset).toBe(0);
    expect(range.endContainer).toBe(textNode(el, 'cd'));
    expect(range.endOffset).toBe(2);
    expect(range.toString()).toBe('cd');
  });

  it('returns null outside the text or for an empty or reversed span', () => {
    const text = readBlockText(block('<p data-kb="0">abc</p>'));
    expect(rangeForBlockOffsets(text, 1, 1)).toBeNull();
    expect(rangeForBlockOffsets(text, 1, 9)).toBeNull();
    expect(rangeForBlockOffsets(text, -1, 1)).toBeNull();
    expect(rangeForBlockOffsets(text, 2, 1)).toBeNull();
    expect(rangeForBlockOffsets(readBlockText(block('<p data-kb="0"></p>')), 0, 0)).toBeNull();
  });
});

describe('enclosingBlock and blockIndexOf', () => {
  it('finds the stamped block above a text node and reads its index', () => {
    const el = block('<div><p data-kb="12">a<b>b</b></p></div>');
    expect(enclosingBlock(textNode(el, 'b'))).toBe(el);
    expect(blockIndexOf(el)).toBe(12);
  });

  it('returns null without a stamp or with a malformed one', () => {
    const host = document.createElement('div');
    host.innerHTML = '<p>x</p><p data-kb="x">y</p>';
    expect(enclosingBlock(host.firstChild!.firstChild!)).toBeNull();
    expect(blockIndexOf(host.lastElementChild!)).toBeNull();
  });
});
