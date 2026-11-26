import {
  LookupReadings,
  ReadingGroup,
  ReadingHint,
  Sense,
  WordEntry,
} from '../../_models/dictionary/dictionary';
import {dictionaryForm, MarkedReadingResult} from './marked-reading';

/** The reading the book's furigana gives an occurrence, in the form the resolver compares */
export interface MarkedReading {
  printed: string;
  /** Hiragana */
  reading: string;
}

/** What the resolver decides from, for one lookup. A part or a phrase leaves `marked`, `choice` and `hint` null */
export interface ResolverInput {
  groups: ReadingGroup[];
  marked: MarkedReading | null;
  choice: string | null;
  hint: ReadingHint | null;
  tokenReading: string | null;
  blind: boolean;
}

/** What a word gives when its data has no `entries`, which version 2 data never has */
export const NO_READINGS: LookupReadings = Object.freeze({
  level: 'certain', source: 'dictionary', shown: null, bookOnly: false, groups: [],
}) as LookupReadings;

/**
 * A word's senses in reading groups. Each entry's readings split into classes of readings that exactly the same
 * senses of the entry apply to, and each class with a sense is a group. Classes keep the order of their first reading.
 * A sense applies to a reading when it has no `r` or its `r` holds the reading
 */
export function readingGroups(entries: readonly WordEntry[] | undefined, senses: readonly Sense[]): ReadingGroup[] {
  const groups: ReadingGroup[] = [];
  for (const entry of entries ?? []) {
    const prefix = `${entry.seq}:`;
    const own = senses.filter(s => s.key.startsWith(prefix));
    const classes = new Map<string, {readings: string[], senses: Sense[]}>();
    for (const reading of entry.readings) {
      const applicable = own.filter(s => s.r === undefined || s.r.includes(reading));
      const signature = applicable.map(s => s.key).join('\n');
      const known = classes.get(signature);
      if (known === undefined) classes.set(signature, {readings: [reading], senses: applicable});
      else known.readings.push(reading);
    }
    for (const {readings, senses: applicable} of classes.values()) {
      if (applicable.length > 0) groups.push({seq: entry.seq, readings, common: entry.common, senses: applicable});
    }
  }
  return groups;
}

/**
 * Orders a word's reading groups for one occurrence, with the group that fits it first, and says how sure that is and
 * where it came from. Furigana that matches a group leaves only that group. Furigana that matches none leaves no
 * group and `bookOnly`
 */
export function resolveReadings(input: ResolverInput): LookupReadings {
  const {groups, marked, choice, hint, tokenReading, blind} = input;
  const has = (reading: string | null) => reading === null ? undefined : groups.find(g => g.readings.includes(reading));

  if (marked !== null) {
    const group = has(marked.reading);
    if (group !== undefined) {
      return {level: 'certain', source: 'furigana', shown: marked.printed, bookOnly: false, groups: [group]};
    }
    return {level: 'certain', source: 'furigana', shown: marked.printed, bookOnly: true, groups: []};
  }
  if (groups.length === 0) {
    if (hint !== null) return {level: 'likely', source: hint.source, shown: hint.printed, bookOnly: false, groups: []};
    return {...NO_READINGS, groups: []};
  }
  if (groups.length === 1) return {level: 'certain', source: 'dictionary', shown: null, bookOnly: false, groups};

  let first: ReadingGroup;
  let source: LookupReadings['source'];
  let level: LookupReadings['level'];
  const chosen = has(choice);
  const hinted = hint === null ? undefined : has(hint.reading);
  const tokenized = has(tokenReading);
  if (chosen !== undefined) {
    [first, source, level] = [chosen, 'choice', 'likely'];
  } else if (hinted !== undefined && hint !== null) {
    [first, source, level] = [hinted, hint.source, 'likely'];
  } else if (tokenized !== undefined) {
    [first, source, level] = [tokenized, 'tokenizer', blind ? 'guess' : 'likely'];
  } else {
    [first, source, level] = [groups[0], 'dictionary', 'guess'];
  }
  return {level, source, shown: null, bookOnly: false, groups: [first, ...groups.filter(g => g !== first)]};
}

/** The dictionary-form pair of a marked reading when it has one, and the surface's reading otherwise */
export function markedForResolver(marked: MarkedReadingResult | null): MarkedReading | null {
  if (marked === null) return null;
  return marked.dictionaryReading !== null && marked.dictionaryPrinted !== null
    ? {printed: marked.dictionaryPrinted, reading: marked.dictionaryReading}
    : {printed: marked.printed, reading: marked.reading};
}

/**
 * An occurrence's reading in dictionary form, with the surface's trailing kana swapped for the lemma's the way
 * MarkedReading does it, so 瞬いた read まばたいた gives まばたく. Falls back to the word's reading when the occurrence
 * has none or its stem isn't the lemma's
 */
export function tokenReadingOf(surface: string, occurrenceReading: string | null, lemma: string,
                               wordReading: string): string {
  if (occurrenceReading === null) return wordReading;
  return dictionaryForm(surface, occurrenceReading, 0, lemma) ?? wordReading;
}

/** A group's identity, which stays the same when the resolver reorders the groups */
export function groupKey(group: ReadingGroup): string {
  return `${group.seq}:${group.readings[0] ?? ''}`;
}
