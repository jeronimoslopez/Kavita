import {DictionaryAnalysis, Sense} from '../../_models/dictionary/dictionary';

export interface OrderedSense {
  /** `sense.key` is the sense's identity. Compare keys, not numbers, because one list can hold several entries */
  sense: Sense;
  /**
   * The sense's place in its own JMdict entry, the `<n>` of its `<seq>:<n>` key. Two senses from different entries
   * can share it. Falls back to the sense's position in the input list when the key doesn't parse
   */
  n: number;
  /** The number to show beside the sense, 1 to N in the order returned */
  label: number;
  /** True for the sense the analysis chose for this occurrence */
  chosen: boolean;
}

function senseNumber(key: string, fallback: number): number {
  const match = /^\d+:(\d+)$/.exec(key);
  return match ? Number(match[1]) : fallback;
}

/**
 * The senses to list, with the one the analysis chose first and the rest in the order given. No analysis, an
 * analysis with no sense, or a key that matches none of the senses leaves the order alone
 */
export function orderSenses(senses: readonly Sense[],
                            analysis: Pick<DictionaryAnalysis, 'senseKey'> | null | undefined): OrderedSense[] {
  const listed = senses.map((sense, i) => ({sense, n: senseNumber(sense.key, i + 1), chosen: false}));
  const key = analysis?.senseKey;
  const at = key == null ? -1 : listed.findIndex(s => s.sense.key === key);
  const ordered = at < 0 ? listed : [{...listed[at], chosen: true}, ...listed.filter((_s, i) => i !== at)];
  return ordered.map((s, i) => ({...s, label: i + 1}));
}
