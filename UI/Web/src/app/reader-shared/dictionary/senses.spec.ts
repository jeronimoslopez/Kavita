import {orderSenses} from './senses';

const senses = [
  {key: '1000004:1', pos: ['n'], gloss: ['taking turns']},
  {key: '1000004:2', pos: ['n'], gloss: ['replacement']},
  {key: '1000004:3', pos: ['n'], gloss: ['shift']},
];
const analysis = (senseKey: string | null) => ({senseKey, reading: null, generator: 'sonnet'});

describe('orderSenses', () => {
  it('puts the sense the analysis chose first and keeps the rest in dictionary order', () => {
    expect(orderSenses(senses, analysis('1000004:3')).map(s => [s.n, s.chosen])).toEqual([
      [3, true],
      [1, false],
      [2, false],
    ]);
  });

  it('leaves the order alone for an analysis with no sense, an unknown key or no analysis', () => {
    for (const a of [analysis(null), analysis('1000004:9'), null, undefined]) {
      expect(orderSenses(senses, a).map(s => [s.n, s.chosen])).toEqual([
        [1, false],
        [2, false],
        [3, false],
      ]);
    }
  });
});
