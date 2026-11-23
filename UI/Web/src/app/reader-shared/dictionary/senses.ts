import {DictionaryAnalysis, Sense} from '../../_models/dictionary/dictionary';

export interface OrderedSense {
  sense: Sense;
  /** The sense's place in the dictionary entry, counted from 1 */
  n: number;
  /** True for the sense the analysis chose for this occurrence */
  chosen: boolean;
}

/**
 * The senses to list, with the one the analysis chose first and the rest in dictionary order. No analysis, an
 * analysis with no sense, or a key that matches none of the senses leaves the order alone
 */
export function orderSenses(senses: readonly Sense[],
                            analysis: Pick<DictionaryAnalysis, 'senseKey'> | null | undefined): OrderedSense[] {
  const listed = senses.map((sense, i) => ({sense, n: i + 1, chosen: false}));
  const key = analysis?.senseKey;
  const at = key == null ? -1 : listed.findIndex(s => s.sense.key === key);
  if (at < 0) return listed;
  return [{...listed[at], chosen: true}, ...listed.filter((_s, i) => i !== at)];
}
