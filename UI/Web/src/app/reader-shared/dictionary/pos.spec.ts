import {posLabel, posLabels} from './pos';

describe('posLabel', () => {
  it('names the common JMdict codes', () => {
    expect(posLabels(['v5s', 'vt'])).toEqual(['godan verb', 'transitive']);
    expect(posLabels(['n', 'vs', 'vi'])).toEqual(['noun', 'suru verb', 'intransitive']);
    expect(posLabels(['adj-na', 'adj-i', 'v1'])).toEqual(['na-adjective', 'i-adjective', 'ichidan verb']);
    expect(posLabel('ctr')).toBe('counter');
    expect(posLabel('exp')).toBe('expression');
  });

  it('groups classical verb classes and drops repeated labels', () => {
    expect(posLabel('v2r-s')).toBe('archaic verb');
    expect(posLabel('v4k')).toBe('archaic verb');
    expect(posLabels(['adj-i', 'adj-ix'])).toEqual(['i-adjective']);
  });

  it('names the nouns, the archaic adjective and the godan class that were missing', () => {
    expect(posLabels(['n-adv', 'n-t', 'n-pr'])).toEqual(['adverbial noun', 'temporal noun', 'proper noun']);
    expect(posLabel('adj-kari')).toBe('archaic adjective');
    expect(posLabel('v5uru')).toBe('godan verb');
  });

  it('falls back to the raw code for anything unmapped', () => {
    expect(posLabel('zzz')).toBe('zzz');
  });

  it('does not read labels off the object prototype', () => {
    expect(posLabel('constructor')).toBe('constructor');
    expect(posLabel('toString')).toBe('toString');
    expect(posLabel('__proto__')).toBe('__proto__');
  });
});
