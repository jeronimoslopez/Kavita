// Short English labels for the JMdict part-of-speech codes the senses carry. Anything not listed shows as its code

const GODAN = 'godan verb';
const ICHIDAN = 'ichidan verb';
const SURU = 'suru verb';
const ARCHAIC_VERB = 'archaic verb';
const ARCHAIC_ADJ = 'archaic adjective';

const POS: ReadonlyMap<string, string> = new Map(Object.entries({
  n: 'noun',
  'n-adv': 'adverbial noun',
  'n-t': 'temporal noun',
  'n-pr': 'proper noun',
  'n-suf': 'noun suffix',
  'n-pref': 'noun prefix',
  pn: 'pronoun',
  num: 'number',
  ctr: 'counter',
  exp: 'expression',
  int: 'interjection',
  conj: 'conjunction',
  prt: 'particle',
  suf: 'suffix',
  pref: 'prefix',
  cop: 'copula',
  aux: 'auxiliary',
  'aux-v': 'auxiliary verb',
  'aux-adj': 'auxiliary adjective',
  unc: 'unclassified',

  adv: 'adverb',
  'adv-to': 'adverb with と',

  'adj-i': 'i-adjective',
  'adj-ix': 'i-adjective',
  'adj-na': 'na-adjective',
  'adj-no': 'takes の',
  'adj-t': 'taru adjective',
  'adj-pn': 'pre-noun',
  'adj-f': 'pre-noun use',
  'adj-ku': ARCHAIC_ADJ,
  'adj-shiku': ARCHAIC_ADJ,
  'adj-nari': ARCHAIC_ADJ,
  'adj-kari': ARCHAIC_ADJ,

  vt: 'transitive',
  vi: 'intransitive',
  v1: ICHIDAN,
  'v1-s': ICHIDAN,
  v5aru: GODAN,
  v5b: GODAN,
  v5g: GODAN,
  v5k: GODAN,
  'v5k-s': GODAN,
  v5m: GODAN,
  v5n: GODAN,
  v5r: GODAN,
  'v5r-i': GODAN,
  v5s: GODAN,
  v5t: GODAN,
  v5u: GODAN,
  'v5u-s': GODAN,
  v5uru: GODAN,
  vs: SURU,
  'vs-s': SURU,
  'vs-i': SURU,
  'vs-c': 'su verb',
  vz: 'zuru verb',
  vk: 'kuru verb',
  vr: 'ru verb, irregular',
  vn: 'nu verb, irregular',
  'v-unspec': 'verb',
}));

/** Codes that start this way are classical verb classes, too rare to list one by one */
const ARCHAIC_VERB_PREFIXES = ['v2', 'v4'];

/** One code as a label, or the code itself when it isn't mapped */
export function posLabel(code: string): string {
  const hit = POS.get(code);
  if (hit !== undefined) return hit;
  if (ARCHAIC_VERB_PREFIXES.some(p => code.startsWith(p))) return ARCHAIC_VERB;
  return code;
}

/** A sense's codes as labels, in order, with repeats dropped */
export function posLabels(codes: readonly string[]): string[] {
  return [...new Set(codes.map(posLabel))];
}
