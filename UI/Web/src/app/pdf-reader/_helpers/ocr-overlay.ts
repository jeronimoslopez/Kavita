import {OcrLine, OcrLineKind, OcrPage} from '../../_models/ocr/ocr';

/** Class of the layer drawn over a pdf.js page. Yomitan's scan selector names it next to pdf.js's .textLayer */
export const OCR_LAYER_CLASS = 'kavita-ocr-layer';

/** Furigana, running heads and page numbers stay stored but aren't drawn, so lookups run straight through the body text */
const DRAWN_KINDS = new Set([OcrLineKind.Body, OcrLineKind.Title, OcrLineKind.Caption]);

export interface OcrLineStyle {
  left: string;
  top: string;
  fontSize: string;
  writingMode: 'vertical-rl' | 'horizontal-tb';
}

/** Class of a find match painted inside a drawn line */
export const OCR_MATCH_CLASS = 'kavita-ocr-match';

/** One find match, or the part of one that falls on a line. Offsets count characters in the line's text */
export interface OcrLineMatch {
  line: number;
  start: number;
  end: number;
  selected: boolean;
}

/**
 * The lines the layer draws, in the order it draws them. Find searches the same lines, so the nth line here is the nth
 * span on the layer.
 */
export function drawnLines(page: OcrPage) {
  return [...page.lines]
    .sort((a, b) => a.order - b.order)
    .filter(line => DRAWN_KINDS.has(line.kind) && line.text.trim().length > 0);
}

/**
 * Positions a line on the layer. The layer is a size container, so cqw and cqh follow the page through every zoom
 * without being recalculated.
 *
 * The font size comes from the box's thickness: its width for a vertical line, its height for a horizontal one.
 * Glyph advances don't match the printed text exactly, so {@link fitLine} stretches each line to its box afterwards.
 */
export function ocrLineStyle(line: OcrLine): OcrLineStyle {
  return {
    left: `${line.x * 100}cqw`,
    top: `${line.y * 100}cqh`,
    fontSize: line.vertical ? `${line.width * 100}cqw` : `${line.height * 100}cqh`,
    writingMode: line.vertical ? 'vertical-rl' : 'horizontal-tb',
  };
}

/**
 * Draws a page's OCR lines over a pdf.js page div, replacing any layer drawn before.
 *
 * pdf.js clears foreign children from a page each time it re-renders it, zoom included, so this runs on every render.
 * Lines go into the DOM in reading order with nothing between them, so a lookup that runs off the end of one line
 * carries on into the next.
 */
export function drawOcrLayer(pageDiv: HTMLElement, page: OcrPage, outlined: boolean): HTMLElement {
  pageDiv.querySelector(`:scope > .${OCR_LAYER_CLASS}`)?.remove();

  const layer = document.createElement('div');
  layer.className = OCR_LAYER_CLASS;
  layer.classList.toggle('outlined', outlined);
  layer.dataset['pageNumber'] = `${page.pageNumber}`;
  layer.dataset['version'] = `${page.version}`;

  const drawn: Array<[HTMLSpanElement, OcrLine]> = [];
  for (const line of drawnLines(page)) {
    const span = document.createElement('span');
    span.textContent = line.text;
    Object.assign(span.style, ocrLineStyle(line));
    // Red to green as the recognizer grows surer of the line's least certain character
    span.style.setProperty('--ocr-hue', `${Math.round(Math.max(0, Math.min(1, line.recognitionConfidence)) * 120)}`);
    layer.appendChild(span);
    drawn.push([span, line]);
  }

  pageDiv.appendChild(layer);
  for (const [span, line] of drawn) fitLine(span, line, layer);
  return layer;
}

/** Shows or hides the confidence outlines on every drawn layer under root */
export function setOcrOutlines(root: ParentNode, outlined: boolean) {
  root.querySelectorAll(`.${OCR_LAYER_CLASS}`).forEach(layer => layer.classList.toggle('outlined', outlined));
}

/**
 * Wraps each match in its line's span, replacing the matches painted before. The wrappers are inline, so they take the
 * line's writing mode and stretch and land on the printed characters without positions of their own.
 *
 * @returns The wrapper of the selected match, if it's on this layer
 */
export function paintOcrMatches(layer: HTMLElement, matches: OcrLineMatch[]): HTMLElement | null {
  const spans = layer.querySelectorAll<HTMLSpanElement>(':scope > span');
  // Setting a span's text drops the wrappers painted inside it
  spans.forEach(span => {
    if (span.childElementCount > 0) span.textContent = span.textContent;
  });

  let selected: HTMLElement | null = null;
  const byLine = new Map<number, OcrLineMatch[]>();
  for (const match of matches) byLine.set(match.line, [...(byLine.get(match.line) ?? []), match]);
  for (const [lineIndex, lineMatches] of byLine) {
    const span = spans[lineIndex];
    if (!span) continue;

    const text = span.textContent ?? '';
    const nodes: Node[] = [];
    let at = 0;
    for (const match of lineMatches.sort((a, b) => a.start - b.start)) {
      // Matches for several words can overlap, and the first one wins
      const start = Math.max(match.start, at);
      if (start >= match.end) continue;
      if (start > at) nodes.push(document.createTextNode(text.slice(at, start)));

      const mark = document.createElement('span');
      mark.className = OCR_MATCH_CLASS;
      mark.classList.toggle('selected', match.selected);
      mark.textContent = text.slice(start, match.end);
      nodes.push(mark);
      if (match.selected && !selected) selected = mark;
      at = match.end;
    }
    if (at < text.length) nodes.push(document.createTextNode(text.slice(at)));
    span.replaceChildren(...nodes);
  }
  return selected;
}

/**
 * Stretches a drawn line along its axis so it ends where the printed line ends. The scale is a ratio of two lengths
 * that both follow the zoom, so it stays right when the page is redrawn larger or smaller.
 */
function fitLine(span: HTMLSpanElement, line: OcrLine, layer: HTMLElement) {
  const rect = span.getBoundingClientRect();
  const target = line.vertical ? line.height * layer.clientHeight : line.width * layer.clientWidth;
  const actual = line.vertical ? rect.height : rect.width;
  if (actual <= 0 || target <= 0) return;
  span.style.transform = line.vertical ? `scaleY(${target / actual})` : `scaleX(${target / actual})`;
}
