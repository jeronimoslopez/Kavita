import {EpubPageBlock, SubToken, Token} from '../../_models/dictionary/dictionary';

/** The token covering a block offset, by binary search over tokens sorted by start. Null in a gap or past the end */
export function tokenAt(tokens: readonly Token[], offset: number): Token | null {
  let lo = 0;
  let hi = tokens.length - 1;
  let found = -1;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    if (tokens[mid][0] <= offset) {
      found = mid;
      lo = mid + 1;
    } else {
      hi = mid - 1;
    }
  }
  if (found < 0) return null;
  const token = tokens[found];
  return offset < token[1] ? token : null;
}

/** The part of a compound covering a block offset, with its index to send back as `sub`. Null for a token with no parts */
export function subAt(token: Token, offset: number): {index: number, sub: SubToken} | null {
  const parts = token[4];
  if (parts === null) return null;
  const index = parts.findIndex(([s, e]) => s <= offset && offset < e);
  return index < 0 ? null : {index, sub: parts[index]};
}

/** True when a token or part has a dictionary word to look up */
export function hasWord(token: Token | SubToken): boolean {
  return token[2] !== null;
}

/** The length check: lookups in a block are off when the text rebuilt from the DOM isn't the length the server stored */
export function blockMatches(block: EpubPageBlock, domTextLength: number): boolean {
  return block.length === domTextLength;
}
