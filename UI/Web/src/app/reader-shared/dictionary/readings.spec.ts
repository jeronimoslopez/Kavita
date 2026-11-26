import {LookupReadings, ReadingHint, Sense, WordEntry} from '../../_models/dictionary/dictionary';
import {markedReadingOf} from './marked-reading';
import {
  groupKey,
  markedForResolver,
  readingGroups,
  resolveReadings,
  ResolverInput,
  tokenReadingOf,
} from './readings';

const sense = (key: string, gloss: string, r?: string[]): Sense =>
  r === undefined ? {key, pos: ['v5k'], gloss: [gloss]} : {key, pos: ['n'], gloss: [gloss], r};

/** 瞬く, which JMdict splits into three entries */
const blinkEntries: WordEntry[] = [
  {seq: 10, common: false, readings: ['しばたたく', 'しばたく']},
  {seq: 20, common: true, readings: ['またたく', 'まばたく']},
  {seq: 30, common: false, readings: ['めたたく', 'めばたく']},
];
const blinkSenses = [
  sense('10:1', 'to blink repeatedly'),
  sense('20:1', 'to twinkle'),
  sense('20:2', 'to blink'),
  sense('30:1', 'to blink (archaic)'),
];
const blink = readingGroups(blinkEntries, blinkSenses);

/** 今日, whose second sense only belongs to こんにち */
const todayEntries: WordEntry[] = [{seq: 40, common: true, readings: ['きょう', 'こんにち', 'こんじつ']}];
const todaySenses = [sense('40:1', 'today'), sense('40:2', 'nowadays', ['こんにち'])];

const sayEntries: WordEntry[] = [{seq: 50, common: true, readings: ['いう', 'ゆう']}];
const say = readingGroups(sayEntries, [sense('50:1', 'to say'), sense('50:2', 'to call')]);

const series = (reading: string): ReadingHint => ({reading, printed: reading, source: 'series'});

function input(overrides: Partial<ResolverInput>): ResolverInput {
  return {groups: blink, marked: null, choice: null, hint: null, tokenReading: null, blind: false, ...overrides};
}

const firsts = (readings: LookupReadings) => readings.groups.map(g => g.readings[0]);

describe('readingGroups', () => {
  it('makes one group per entry when no sense is restricted, in entry order', () => {
    expect(blink).toEqual([
      {seq: 10, readings: ['しばたたく', 'しばたく'], common: false, senses: [blinkSenses[0]]},
      {seq: 20, readings: ['またたく', 'まばたく'], common: true, senses: [blinkSenses[1], blinkSenses[2]]},
      {seq: 30, readings: ['めたたく', 'めばたく'], common: false, senses: [blinkSenses[3]]},
    ]);
  });

  it('splits an entry whose senses are restricted by reading into a group per set of senses', () => {
    expect(readingGroups(todayEntries, todaySenses)).toEqual([
      {seq: 40, readings: ['きょう', 'こんじつ'], common: true, senses: [todaySenses[0]]},
      {seq: 40, readings: ['こんにち'], common: true, senses: todaySenses},
    ]);
  });

  it('drops a class no sense applies to and senses of entries the word does not list', () => {
    const groups = readingGroups(
      [{seq: 60, common: false, readings: ['あ', 'い']}],
      [sense('60:1', 'only あ', ['あ']), sense('99:1', 'another word')],
    );
    expect(groups).toEqual([{seq: 60, readings: ['あ'], common: false, senses: [sense('60:1', 'only あ', ['あ'])]}]);
  });

  it('gives no groups without entries', () => {
    expect(readingGroups(undefined, blinkSenses)).toEqual([]);
    expect(readingGroups([], blinkSenses)).toEqual([]);
  });

  it('keys a group by its entry and first reading', () => {
    expect(blink.map(groupKey)).toEqual(['10:しばたたく', '20:またたく', '30:めたたく']);
  });
});

describe('resolveReadings', () => {
  const cases: {name: string, input: ResolverInput, level: string, source: string, shown: string | null,
    pick: string | null, bookOnly: boolean, firsts: string[]}[] = [
    {
      name: '瞬く read しばたたく by fixed cost is a guess with that group first',
      input: input({tokenReading: 'しばたたく', blind: true}),
      level: 'guess', source: 'tokenizer', shown: null, pick: 'しばたたく', bookOnly: false, firsts: ['しばたたく', 'またたく', 'めたたく'],
    },
    {
      name: '瞬く read しばたたく by the tokenizer is likely',
      input: input({tokenReading: 'しばたたく'}),
      level: 'likely', source: 'tokenizer', shown: null, pick: 'しばたたく', bookOnly: false, firsts: ['しばたたく', 'またたく', 'めたたく'],
    },
    {
      name: '瞬く with a series hint of またたく is likely from the series, that group first',
      input: input({tokenReading: 'しばたたく', blind: true, hint: series('またたく')}),
      level: 'likely', source: 'series', shown: null, pick: 'またたく', bookOnly: false, firsts: ['またたく', 'しばたたく', 'めたたく'],
    },
    {
      name: 'a hint matches any reading of a group, not only its first',
      input: input({hint: {reading: 'めばたく', printed: 'めばたく', source: 'book'}}),
      level: 'likely', source: 'book', shown: null, pick: 'めばたく', bookOnly: false, firsts: ['めたたく', 'しばたたく', 'またたく'],
    },
    {
      name: 'a saved choice beats a hint',
      input: input({choice: 'めたたく', hint: series('またたく'), tokenReading: 'しばたたく'}),
      level: 'likely', source: 'choice', shown: null, pick: 'めたたく', bookOnly: false, firsts: ['めたたく', 'しばたたく', 'またたく'],
    },
    {
      name: 'a saved choice that names a later reading of a group still matches that group',
      input: input({choice: 'まばたく', tokenReading: 'しばたたく'}),
      level: 'likely', source: 'choice', shown: null, pick: 'まばたく', bookOnly: false, firsts: ['またたく', 'しばたたく', 'めたたく'],
    },
    {
      name: '言う read by the tokenizer as a reading its group lacks picks nothing',
      input: input({groups: say, tokenReading: 'いわ'}),
      level: 'certain', source: 'dictionary', shown: null, pick: null, bookOnly: false, firsts: ['いう'],
    },
    {
      name: 'a choice no group has is skipped',
      input: input({choice: 'ちがう', hint: series('またたく')}),
      level: 'likely', source: 'series', shown: null, pick: 'またたく', bookOnly: false, firsts: ['またたく', 'しばたたく', 'めたたく'],
    },
    {
      name: 'a hint whose reading no group has is ignored',
      input: input({hint: series('メイク'), tokenReading: 'まばたく'}),
      level: 'likely', source: 'tokenizer', shown: null, pick: 'まばたく', bookOnly: false, firsts: ['またたく', 'しばたたく', 'めたたく'],
    },
    {
      name: 'nothing that matches a group falls back to the dictionary order as a guess',
      input: input({tokenReading: 'ちがう'}),
      level: 'guess', source: 'dictionary', shown: null, pick: 'しばたたく', bookOnly: false, firsts: ['しばたたく', 'またたく', 'めたたく'],
    },
    {
      name: 'furigana that matches a group leaves only that group, certain',
      input: input({marked: {printed: 'マバタク', reading: 'まばたく'}, choice: 'しばたたく'}),
      level: 'certain', source: 'furigana', shown: 'マバタク', pick: 'まばたく', bookOnly: false, firsts: ['またたく'],
    },
    {
      name: 'ねえちゃん on 小姐 matches no group, so the book reading shows over every sense',
      input: input({
        groups: readingGroups([{seq: 70, common: false, readings: ['しょうしゃ']}], [sense('70:1', 'young lady')]),
        marked: {printed: 'ねえちゃん', reading: 'ねえちゃん'},
      }),
      level: 'certain', source: 'furigana', shown: 'ねえちゃん', pick: null, bookOnly: true, firsts: [],
    },
    {
      name: '言う with one entry is one group, certain',
      input: input({groups: say, tokenReading: 'ゆう', hint: series('ゆう'), choice: 'ゆう', blind: true}),
      level: 'certain', source: 'dictionary', shown: null, pick: 'ゆう', bookOnly: false, firsts: ['いう'],
    },
    {
      name: '今日 restricted by r has two groups, and the tokenizer picks こんにち',
      input: input({groups: readingGroups(todayEntries, todaySenses), tokenReading: 'こんにち'}),
      level: 'likely', source: 'tokenizer', shown: null, pick: 'こんにち', bookOnly: false, firsts: ['こんにち', 'きょう'],
    },
    {
      name: 'a word without groups takes a hint as likely',
      input: input({groups: [], hint: {reading: 'がおしゅん', printed: 'ガオシュン', source: 'book'}}),
      level: 'likely', source: 'book', shown: 'ガオシュン', pick: null, bookOnly: false, firsts: [],
    },
    {
      name: 'a word without groups or a hint is the dictionary, certain',
      input: input({groups: [], tokenReading: 'よみ', choice: 'よみ'}),
      level: 'certain', source: 'dictionary', shown: null, pick: null, bookOnly: false, firsts: [],
    },
  ];

  for (const c of cases) {
    it(c.name, () => {
      const readings = resolveReadings(c.input);
      const {groups: _groups, ...decision} = readings;
      expect(decision).toEqual({level: c.level, source: c.source, shown: c.shown, pick: c.pick, bookOnly: c.bookOnly});
      expect(firsts(readings)).toEqual(c.firsts);
    });
  }

  it('keeps the other groups as they are, in their original order', () => {
    const readings = resolveReadings(input({tokenReading: 'めばたく'}));
    expect(readings.groups).toEqual([blink[2], blink[0], blink[1]]);
    expect(readings.groups[0]).toBe(blink[2]);
  });
});

describe('markedForResolver', () => {
  it('takes the dictionary form when there is one and the surface reading otherwise', () => {
    expect(markedForResolver(markedReadingOf('言った', 0, 2, [[0, 1, 'い']], '言う'))).toEqual({printed: 'いう', reading: 'いう'});
    expect(markedForResolver(markedReadingOf('云った', 0, 2, [[0, 1, 'い']], '言う'))).toEqual({printed: 'いっ', reading: 'いっ'});
    expect(markedForResolver(markedReadingOf('高順は', 0, 2, [[0, 2, 'ガオシュン']], '高順')))
      .toEqual({printed: 'ガオシュン', reading: 'がおしゅん'});
    expect(markedForResolver(null)).toBeNull();
  });

  it('feeds the resolver a certain reading for 言う marked い', () => {
    const marked = markedForResolver(markedReadingOf('言う', 0, 2, [[0, 1, 'い']], '言う'));
    const readings = resolveReadings(input({groups: say, marked}));
    expect(readings).toEqual({
      level: 'certain', source: 'furigana', shown: 'いう', pick: 'いう', bookOnly: false, groups: [say[0]],
    });
  });
});

describe('tokenReadingOf', () => {
  const cases: [surface: string, r: string | null, lemma: string, expected: string][] = [
    ['瞬く', 'しばたたく', '瞬く', 'しばたたく'],
    ['瞬いた', 'まばたいた', '瞬く', 'まばたく'],
    ['食べた', 'たべた', '食べる', 'たべる'],
    ['云った', 'いった', '言う', 'いう'],
    ['瞬く', null, '瞬く', 'いう'],
    ['いった', 'いった', '言う', 'いう'],
  ];

  for (const [surface, r, lemma, expected] of cases) {
    it(`reads ${surface} read ${r} with the lemma ${lemma} as ${expected}`, () => {
      expect(tokenReadingOf(surface, r, lemma, 'いう')).toBe(expected);
    });
  }
});
