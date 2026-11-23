/**
 * The client half of the EPUB block contract. The server's EpubTextBlocks builds each block's text the same way,
 * so token offsets from the API line up with offsets computed here.
 */

/** Elements whose text isn't part of a block. Ruby readings would otherwise split the base text's offsets */
const SKIPPED_TAGS = new Set(['rt', 'rp', 'script', 'style']);

export const BLOCK_ATTRIBUTE = 'data-kb';

export interface BlockText {
  element: Element;
  /** The block's text, the concatenation of `nodes` */
  text: string;
  /** Non-empty text nodes in document order */
  nodes: Text[];
  /** UTF-16 offset in `text` where each of `nodes` starts */
  starts: number[];
}

/** The nearest element stamped `data-kb` at or above a node, or null */
export function enclosingBlock(node: Node): Element | null {
  const element = node.nodeType === Node.ELEMENT_NODE ? node as Element : node.parentElement;
  return element?.closest(`[${BLOCK_ATTRIBUTE}]`) ?? null;
}

/** A block's `data-kb` value as a number, or null when it's missing or not an integer */
export function blockIndexOf(block: Element): number | null {
  const value = block.getAttribute(BLOCK_ATTRIBUTE);
  if (value === null || !/^\d+$/.test(value)) return null;
  return Number(value);
}

/** Walks a block's text nodes in document order, leaving out everything inside rt, rp, script and style */
export function readBlockText(block: Element): BlockText {
  const nodes: Text[] = [];
  const starts: number[] = [];
  let text = '';
  const visit = (parent: Node) => {
    for (let child = parent.firstChild; child !== null; child = child.nextSibling) {
      if (child.nodeType === Node.TEXT_NODE) {
        const data = (child as Text).data;
        if (data.length === 0) continue;
        nodes.push(child as Text);
        starts.push(text.length);
        text += data;
      } else if (child.nodeType === Node.ELEMENT_NODE && !SKIPPED_TAGS.has((child as Element).localName.toLowerCase())) {
        visit(child);
      }
    }
  };
  visit(block);
  return {element: block, text, nodes, starts};
}

/** The block offset of a position in one of the block's text nodes, or null for a node the walk left out */
export function blockOffsetOf(block: BlockText, node: Node, offset: number): number | null {
  const i = block.nodes.indexOf(node as Text);
  if (i < 0) return null;
  const length = block.nodes[i].data.length;
  return block.starts[i] + Math.max(0, Math.min(offset, length));
}

/**
 * The text node and offset in it for a block offset. At a boundary between two nodes, `preferEnd` picks the end of
 * the earlier node, which is what a range's end wants, and otherwise the start of the later one
 */
export function domPositionOf(block: BlockText, offset: number, preferEnd = false): {node: Text, offset: number} | null {
  const {nodes, starts} = block;
  if (nodes.length === 0 || offset < 0 || offset > block.text.length) return null;
  // The last node starting at or before the offset, or strictly before it when preferEnd is set
  let lo = 0;
  let hi = nodes.length - 1;
  let found = 0;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    if (preferEnd ? starts[mid] < offset : starts[mid] <= offset) {
      found = mid;
      lo = mid + 1;
    } else {
      hi = mid - 1;
    }
  }
  const node = nodes[found];
  return {node, offset: Math.min(offset - starts[found], node.data.length)};
}

/**
 * A DOM Range over block offsets `[s, e)`. It can start and end in different text nodes and cross inline wrappers
 * such as ruby or a highlight. Null when the offsets fall outside the block's text or the span is empty
 */
export function rangeForBlockOffsets(block: BlockText, s: number, e: number): Range | null {
  if (e <= s) return null;
  const start = domPositionOf(block, s);
  const end = domPositionOf(block, e, true);
  if (start === null || end === null) return null;
  const range = block.element.ownerDocument.createRange();
  range.setStart(start.node, start.offset);
  range.setEnd(end.node, end.offset);
  return range;
}
