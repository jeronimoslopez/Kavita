import {DictionaryLookupResult, EpubPageTokens} from '../../_models/dictionary/dictionary';
import parity from './fixtures/lookup-parity.json';
import {carriesSenses, localLookup, LocalLookupRequest} from './local-lookup';

/**
 * Written by the server's ParityFixture_MatchesThePageResponseAndTheLookupAnswers test from its own page response
 * and lookup answers. Run that test with KAVITA_UPDATE_FIXTURES=1 to write it again
 */
const fixture = parity as unknown as {
  page: EpubPageTokens;
  texts: Record<string, string>;
  taps: {request: LocalLookupRequest, result: DictionaryLookupResult | null}[];
};

function page(overrides: Partial<EpubPageTokens> = {}): EpubPageTokens {
  return {
    version: 'v1',
    blocks: [{index: 0, length: 3, tokens: [[0, 3, 1, 'にほんご', null]], phrases: [[0, 3, 2]]}],
    words: {
      1: {lemma: '日本語', reading: 'にほんご', jlpt: 5, pos: '名詞', readings: ['にほんご'], senses: [{key: '1:1', pos: ['n'], gloss: ['Japanese']}]},
      2: {lemma: '日本語', reading: 'にほんご', jlpt: null, pos: '表現', readings: [], senses: []},
    },
    analysis: [],
    ...overrides,
  };
}

describe('localLookup', () => {
  it('covers every kind of tap in the fixture', () => {
    const answered = fixture.taps.filter(t => t.result !== null);
    expect(answered.length).toBeGreaterThan(10);
    expect(fixture.taps.length - answered.length).toBeGreaterThan(10);
    expect(answered.some(t => t.result!.analysis !== null)).toBe(true);
    expect(answered.some(t => t.result!.parts.length > 0 && t.request.sub !== null)).toBe(true);
    expect(answered.some(t => (t.result!.partOf ?? []).length > 1)).toBe(true);
    expect(answered.some(t => t.request.phrase !== null)).toBe(true);
    expect(answered.some(t => t.result!.word.isProper)).toBe(true);
  });

  for (const {request, result} of fixture.taps) {
    it(`gives the server's answer for block ${request.block}, s ${request.s}, sub ${request.sub}, phrase ${request.phrase}`, () => {
      expect(localLookup(fixture.page, request, fixture.texts[request.block] ?? '')).toEqual(result);
    });
  }

  it('reads a page from the fixture as carrying senses', () => {
    expect(carriesSenses(fixture.page)).toBe(true);
  });

  it('reads words without senses as an older server, and a page without words as carrying them', () => {
    const old = page({words: {1: {lemma: '日本語', reading: 'にほんご', jlpt: 5}}});
    expect(carriesSenses(old)).toBe(false);
    expect(carriesSenses(page({words: {}}))).toBe(true);
    expect(localLookup(page({words: {}}), {block: 0, s: 0, sub: null}, '日本語')).toBeNull();
  });

  it('fills fields a word or analysis leaves out the way the server would send them', () => {
    const sparse = page({
      words: {1: {lemma: '日本語', reading: 'にほんご', jlpt: null, senses: []}},
      analysis: [{block: 0, s: 0, senseKey: null, reading: null}],
    });

    const result = localLookup(sparse, {block: 0, s: 0, sub: null}, '日本語');

    expect(result?.word).toEqual({
      id: 1, surface: '日本語', lemma: '日本語', pos: '', isProper: false, occurrenceReading: 'にほんご', reading: 'にほんご',
      readings: [], jlpt: null,
    });
    expect(result?.analysis).toEqual({senseKey: null, reading: null, generator: ''});
    expect(result?.partOf).toEqual([]);
  });

  it('takes the first analysis entry for an occurrence, as the server takes the first row', () => {
    const twice = page({analysis: [
      {block: 0, s: 0, senseKey: '1:1', reading: null, generator: 'first'},
      {block: 0, s: 0, senseKey: null, reading: null, generator: 'second'},
    ]});

    expect(localLookup(twice, {block: 0, s: 0, sub: null}, '日本語')?.analysis?.generator).toBe('first');
  });

  it('treats a missing phrase as a word lookup and a block without phrases as having none', () => {
    const noPhrases = page({blocks: [{index: 0, length: 3, tokens: [[0, 3, 1, 'にほんご', null]]}]});

    expect(localLookup(noPhrases, {block: 0, s: 0, sub: null}, '日本語')?.partOf).toEqual([]);
    expect(localLookup(noPhrases, {block: 0, s: 0, sub: null, phrase: 0}, '日本語')).toBeNull();
    expect(localLookup(page(), {block: 0, s: 0, sub: null, phrase: undefined}, '日本語')?.word.id).toBe(1);
  });

  it('clamps offsets past the text instead of counting from its end', () => {
    expect(localLookup(page(), {block: 0, s: 0, sub: null}, '日本')?.word.surface).toBe('日本');
    expect(localLookup(page(), {block: 0, s: 0, sub: null}, '')?.word.surface).toBe('');
  });
});
