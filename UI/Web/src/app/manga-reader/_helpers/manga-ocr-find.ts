import {OcrPage} from '../../_models/ocr/ocr';
import {drawnLines, OcrLineMatch} from '../../reader-shared/_helpers/ocr-overlay';

/** One hit of a search, as offsets into a page's drawn lines joined with nothing between them */
export interface OcrFindMatch {
  /** 1-based, like the OCR endpoints */
  pageNumber: number;
  start: number;
  end: number;
}

/** Folds the differences a reader doesn't type: full-width and half-width forms, letter case and spaces */
function fold(text: string) {
  return Array.from(text, ch => {
    if (/\s/.test(ch)) return '';
    const folded = ch.normalize('NFKC').toLowerCase();
    return folded.length === 1 ? folded : ch;
  });
}

/**
 * Finds every occurrence of the query on the pages, in page order. A match can run across the columns of a bubble,
 * because a page's lines are searched as one string.
 */
export function findOcrMatches(pages: OcrPage[], query: string): OcrFindMatch[] {
  const needle = fold(query).filter(ch => ch !== '');
  if (needle.length === 0) return [];

  const matches: OcrFindMatch[] = [];
  for (const page of [...pages].sort((a, b) => a.pageNumber - b.pageNumber)) {
    // Characters and offsets are counted in UTF-16 units, the way the drawn spans count them, and spaces drop out of
    // the search, so each kept character remembers where it starts and ends in the page text
    const kept: Array<{ch: string, start: number, end: number}> = [];
    let unit = 0;
    for (const original of drawnLines(page).map(line => line.text).join('')) {
      const [ch] = fold(original);
      if (ch !== '') kept.push({ch, start: unit, end: unit + original.length});
      unit += original.length;
    }

    for (let at = 0; at + needle.length <= kept.length; at++) {
      if (!needle.every((ch, k) => kept[at + k].ch === ch)) continue;
      matches.push({pageNumber: page.pageNumber, start: kept[at].start, end: kept[at + needle.length - 1].end});
      at += needle.length - 1;
    }
  }
  return matches;
}

/** Splits the page's matches into the parts that fall on each drawn line, for paintOcrMatches */
export function lineMatches(page: OcrPage, matches: OcrFindMatch[], selected: OcrFindMatch | null): OcrLineMatch[] {
  const lines = drawnLines(page);
  const lineStarts: number[] = [];
  let at = 0;
  for (const line of lines) {
    lineStarts.push(at);
    at += line.text.length;
  }

  const result: OcrLineMatch[] = [];
  for (const match of matches) {
    if (match.pageNumber !== page.pageNumber) continue;
    const isSelected = match === selected;
    lines.forEach((line, k) => {
      const from = Math.max(match.start, lineStarts[k]) - lineStarts[k];
      const to = Math.min(match.end, lineStarts[k] + line.text.length) - lineStarts[k];
      if (from < to) result.push({line: k, start: from, end: to, selected: isSelected});
    });
  }
  return result;
}
