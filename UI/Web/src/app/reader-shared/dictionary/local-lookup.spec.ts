import {DictionaryLookupResult, EpubPageTokens, EpubPageWord, Sense} from '../../_models/dictionary/dictionary';
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
      const local = localLookup(fixture.page, request, fixture.texts[request.block] ?? '');
      // A fixture written before the server sent readings has none to compare. toEqual skips an undefined field
      const compared = local !== null && result !== null && result.readings === undefined
        ? {...local, readings: undefined}
        : local;
      expect(compared).toEqual(result);
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

  describe('readings', () => {
    const sense = (key: string, gloss: string): Sense => ({key, pos: ['v5k'], gloss: [gloss]});
    const blink: EpubPageWord = {
      lemma: '瞬く', reading: 'またたく', jlpt: 1, pos: '動詞', readings: ['またたく', 'まばたく', 'しばたたく'],
      senses: [sense('10:1', 'to blink repeatedly'), sense('20:1', 'to twinkle'), sense('30:1', 'to blink (archaic)')],
      entries: [
        {seq: 10, common: false, readings: ['しばたたく', 'しばたく']},
        {seq: 20, common: true, readings: ['またたく', 'まばたく']},
        {seq: 30, common: false, readings: ['めたたく', 'めばたく']},
      ],
    };
    const eye: EpubPageWord = {
      lemma: '目', reading: 'め', jlpt: 5, pos: '名詞', readings: ['め'], senses: [sense('40:1', 'eye')],
      entries: [{seq: 40, common: true, readings: ['め']}],
    };
    const phrase: EpubPageWord = {
      lemma: '目を瞬く', reading: 'めをしばたたく', jlpt: null, pos: '表現', readings: [], senses: [sense('50:1', 'to blink')],
      entries: [{seq: 50, common: false, readings: ['めをしばたたく']}],
    };

    /** 目を瞬いた, with 瞬いた at 2 to 5 read しばたたいた, the compound 目を瞬いた at 0 to 5 is not a token */
    function blinkPage(word: Partial<EpubPageWord> = {}, block: Partial<EpubPageTokens['blocks'][0]> = {}): EpubPageTokens {
      return {
        version: 'v3', seriesId: 7,
        blocks: [{
          index: 0, length: 5,
          tokens: [[0, 1, 2, 'め', null], [1, 2, null, null, null], [2, 5, 1, 'しばたたいた', [[2, 3, 1, 'しばたた'], [3, 5, null, null]]]],
          phrases: [[0, 5, 3]],
          ...block,
        }],
        words: {1: {...blink, ...word}, 2: eye, 3: phrase},
        analysis: [],
      };
    }
    const text = '目を瞬いた';
    const tap = (page: EpubPageTokens, sub: number | null = null, phraseIndex: number | null = null) =>
      localLookup(page, {block: 0, s: 2, sub, phrase: phraseIndex}, text)?.readings;
    const firsts = (page: EpubPageTokens, sub: number | null = null) => tap(page, sub)?.groups.map(g => g.readings[0]);

    it('orders the groups by the token reading in dictionary form and calls a blind reading a guess', () => {
      expect(tap(blinkPage(), null)).toMatchObject({level: 'likely', source: 'tokenizer', shown: null, bookOnly: false});
      expect(firsts(blinkPage())).toEqual(['しばたたく', 'またたく', 'めたたく']);
      expect(tap(blinkPage({}, {blind: [0, 2]}))).toMatchObject({level: 'guess', source: 'tokenizer'});
      expect(tap(blinkPage({}, {blind: [0]}))).toMatchObject({level: 'likely'});
    });

    it('puts the hint and then the saved choice ahead of the tokenizer', () => {
      const hint = {reading: 'またたく', printed: 'またたく', source: 'series' as const};
      expect(tap(blinkPage({hint}, {blind: [2]}))).toMatchObject({level: 'likely', source: 'series'});
      expect(firsts(blinkPage({hint}))).toEqual(['またたく', 'しばたたく', 'めたたく']);

      expect(tap(blinkPage({hint, choice: 'めたたく'}))).toMatchObject({level: 'likely', source: 'choice'});
      expect(firsts(blinkPage({hint, choice: 'めたたく'}))).toEqual(['めたたく', 'しばたたく', 'またたく']);
    });

    it('takes the book furigana over everything and shows it in the dictionary form', () => {
      const page = blinkPage({choice: 'しばたたく'}, {ruby: [[2, 3, 'まばた']]});
      expect(tap(page)).toEqual({
        level: 'certain', source: 'furigana', shown: 'まばたく', bookOnly: false,
        groups: [{seq: 20, readings: ['またたく', 'まばたく'], common: true, senses: [blink.senses![1]]}],
      });
    });

    it('lists every sense ungrouped when the furigana matches no group', () => {
      expect(tap(blinkPage({}, {ruby: [[2, 3, 'ぱちぱ']]}))).toEqual({
        level: 'certain', source: 'furigana', shown: 'ぱちぱく', bookOnly: true, groups: [],
      });
    });

    it('resolves a part from its own reading and nothing else', () => {
      const page = blinkPage({choice: 'めたたく', hint: {reading: 'またたく', printed: 'またたく', source: 'book'}},
        {ruby: [[2, 3, 'まばた']], blind: [2]});
      expect(tap(page, 0)).toMatchObject({level: 'likely', source: 'tokenizer', shown: null, bookOnly: false});
      expect(firsts(page, 0)).toEqual(['しばたたく', 'またたく', 'めたたく']);
    });

    it('resolves a phrase from its word reading', () => {
      expect(tap(blinkPage(), null, 0)).toMatchObject({level: 'certain', source: 'dictionary', shown: null, groups: [{seq: 50}]});
    });

    it('gives a word without entries no groups even where the book has furigana', () => {
      const old = blinkPage({entries: undefined}, {ruby: [[2, 3, 'まばた']], blind: [2]});
      const result = localLookup(old, {block: 0, s: 2, sub: null}, text);
      expect(result?.readings).toEqual({level: 'certain', source: 'dictionary', shown: null, bookOnly: false, groups: []});
      expect(result?.senses).toEqual(blink.senses);
      expect(result?.word.reading).toBe('またたく');
    });

    it('gives a word with an empty entry list the resolver answer for no groups', () => {
      const page = blinkPage({entries: [], hint: {reading: 'がおしゅん', printed: 'ガオシュン', source: 'book'}});
      expect(tap(page)).toEqual({level: 'likely', source: 'book', shown: 'ガオシュン', bookOnly: false, groups: []});
    });
  });
});
