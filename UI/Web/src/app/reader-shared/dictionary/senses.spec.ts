import {orderSenses} from './senses';

const senses = [
  {key: '1000004:1', pos: ['n'], gloss: ['taking turns']},
  {key: '1000004:2', pos: ['n'], gloss: ['replacement']},
  {key: '1000004:3', pos: ['n'], gloss: ['shift']},
];
const analysis = (senseKey: string | null) => ({senseKey, reading: null, generator: 'sonnet'});

describe('orderSenses', () => {
  it('puts the sense the analysis chose first and keeps the rest in the order given', () => {
    expect(orderSenses(senses, analysis('1000004:3')).map(s => [s.n, s.label, s.chosen])).toEqual([
      [3, 1, true],
      [1, 2, false],
      [2, 3, false],
    ]);
  });

  it('leaves the order alone for an analysis with no sense, an unknown key or no analysis', () => {
    for (const a of [analysis(null), analysis('1000004:9'), null, undefined]) {
      expect(orderSenses(senses, a).map(s => [s.n, s.label, s.chosen])).toEqual([
        [1, 1, false],
        [2, 2, false],
        [3, 3, false],
      ]);
    }
  });

  it('reads n from the key when the list holds senses from several entries', () => {
    const mixed = [
      {key: '1365860:1', pos: ['n'], gloss: ['a']},
      {key: '1365860:2', pos: ['n'], gloss: ['b']},
      {key: '1365850:1', pos: ['n'], gloss: ['c']},
    ];
    expect(orderSenses(mixed, analysis('1365850:1')).map(s => [s.sense.key, s.n, s.label, s.chosen])).toEqual([
      ['1365850:1', 1, 1, true],
      ['1365860:1', 1, 2, false],
      ['1365860:2', 2, 3, false],
    ]);
  });

  it('falls back to the list position for a key that does not parse', () => {
    const odd = [senses[0], {key: 'odd', pos: [], gloss: ['x']}];
    expect(orderSenses(odd, null).map(s => s.n)).toEqual([1, 2]);
  });
});
