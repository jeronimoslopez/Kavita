import {clearOpenWord, HighlightEnv, markOpenWord, OPEN_WORD_HIGHLIGHT, supportsHighlights} from './highlight';

class FakeHighlight {
  readonly ranges: Range[];
  constructor(...ranges: Range[]) {
    this.ranges = ranges;
  }
}

function env(): HighlightEnv & {CSS: {highlights: Map<string, unknown>}} {
  return {CSS: {highlights: new Map()}, Highlight: FakeHighlight};
}

describe('open word highlight', () => {
  it('sets and clears the named highlight', () => {
    const fake = env();
    const first = document.createRange();
    const second = document.createRange();
    markOpenWord(first, fake);
    markOpenWord(second, fake);
    const highlight = fake.CSS.highlights.get(OPEN_WORD_HIGHLIGHT) as FakeHighlight;
    expect(highlight.ranges).toEqual([second]);
    expect(fake.CSS.highlights.size).toBe(1);

    clearOpenWord(fake);
    expect(fake.CSS.highlights.has(OPEN_WORD_HIGHLIGHT)).toBe(false);
  });

  it('does nothing where the API is missing', () => {
    const range = document.createRange();
    expect(supportsHighlights({})).toBe(false);
    expect(supportsHighlights({CSS: {highlights: new Map()}})).toBe(false);
    expect(() => markOpenWord(range, {})).not.toThrow();
    expect(() => clearOpenWord({CSS: {}})).not.toThrow();
    expect(supportsHighlights(env())).toBe(true);
  });

  it('is named for the stylesheet', () => {
    expect(OPEN_WORD_HIGHLIGHT).toBe('kavita-dict-open');
  });
});
