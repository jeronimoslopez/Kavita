import {Token} from '../../_models/dictionary/dictionary';
import {blockMatches, hasWord, subAt, tokenAt} from './tokens';

const compound: Token = [3, 7, 2, 'しんぶんしゃ', [[3, 5, 3, 'しんぶん'], [5, 7, null, null]]];
const tokens: Token[] = [
  [0, 2, 0, 'きょう', null],
  [2, 3, null, null, null],
  compound,
  [9, 10, 1, null, null],
];

describe('tokenAt', () => {
  it('finds the token covering an offset, including both ends of the block', () => {
    expect(tokenAt(tokens, 0)).toBe(tokens[0]);
    expect(tokenAt(tokens, 1)).toBe(tokens[0]);
    expect(tokenAt(tokens, 2)).toBe(tokens[1]);
    expect(tokenAt(tokens, 6)).toBe(compound);
    expect(tokenAt(tokens, 9)).toBe(tokens[3]);
  });

  it('returns null in a gap, past the end, or with no tokens', () => {
    expect(tokenAt(tokens, 7)).toBeNull();
    expect(tokenAt(tokens, 10)).toBeNull();
    expect(tokenAt(tokens, -1)).toBeNull();
    expect(tokenAt([], 0)).toBeNull();
  });
});

describe('subAt', () => {
  it('returns the part covering an offset with its index', () => {
    expect(subAt(compound, 4)).toEqual({index: 0, sub: compound[4]![0]});
    expect(subAt(compound, 5)).toEqual({index: 1, sub: compound[4]![1]});
  });

  it('returns null for a token without parts or an offset outside them', () => {
    expect(subAt(tokens[0], 0)).toBeNull();
    expect(subAt(compound, 8)).toBeNull();
  });
});

describe('hasWord', () => {
  it('is false for a token or part with no word', () => {
    expect(hasWord(tokens[0])).toBe(true);
    expect(hasWord(tokens[1])).toBe(false);
    expect(hasWord(compound[4]![1])).toBe(false);
  });
});

describe('blockMatches', () => {
  it('compares the stored length with the length rebuilt from the DOM', () => {
    expect(blockMatches({index: 0, length: 10, tokens}, 10)).toBe(true);
    expect(blockMatches({index: 0, length: 10, tokens}, 11)).toBe(false);
  });
});
