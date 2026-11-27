import {
  LookupReadings,
  ReadingGroup,
  ReadingHint,
  Sense,
  WordEntry,
} from '../../_models/dictionary/dictionary';
import {dictionaryForm, isKanji, MarkedReadingResult, toHiraganaServer} from './marked-reading';

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
  level: 'certain', source: 'dictionary', shown: null, pick: null, bookOnly: false, groups: [],
}) as LookupReadings;

/**
 * A word's senses in reading groups. Each entry's readings split into classes of readings that exactly the same
 * senses of the entry apply to, and each class with a sense is a group. Classes keep the order of their first reading.
 * A sense applies to a reading when it has no `r` or its `r` holds the reading. Groups with equal readings lists then
 * merge into the first of them, since their tabs would offer no choice of reading. A word whose lemma has no kanji has
 * one group, read as the lemma in hiragana, because its reading is what the book prints. A null `lemma` groups by the
 * entries alone
 */
export function readingGroups(entries: readonly WordEntry[] | undefined, senses: readonly Sense[],
                              lemma: string | null = null): ReadingGroup[] {
  if (entries === undefined || entries.length === 0) return [];
  if (lemma !== null && !hasKanji(lemma)) {
    return [{
      seq: entries[0].seq, readings: [toHiraganaServer(lemma)], common: entries.some(e => e.common),
      senses: [...senses],
    }];
  }

  const groups: ReadingGroup[] = [];
  for (const entry of entries) {
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
  return mergeEqualReadings(groups);
}

/**
 * Folds each group into the first group whose readings are the same in the same order. The merged group sits where
 * that first group sat, keeps its seq, holds the senses of all of them in order, and is common when any is
 */
function mergeEqualReadings(groups: readonly ReadingGroup[]): ReadingGroup[] {
  const merged: ReadingGroup[] = [];
  const at = new Map<string, number>();
  for (const group of groups) {
    const signature = group.readings.join('\n');
    const index = at.get(signature);
    if (index === undefined) {
      at.set(signature, merged.length);
      merged.push(group);
      continue;
    }
    const first = merged[index];
    merged[index] = {...first, common: first.common || group.common, senses: [...first.senses, ...group.senses]};
  }
  return merged;
}

function hasKanji(text: string): boolean {
  for (const ch of text) {
    if (isKanji(ch.codePointAt(0) ?? 0)) return true;
  }
  return false;
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
      return {
        level: 'certain', source: 'furigana', shown: marked.printed, pick: marked.reading, bookOnly: false,
        groups: [group],
      };
    }
    return {level: 'certain', source: 'furigana', shown: marked.printed, pick: null, bookOnly: true, groups: []};
  }
  if (groups.length === 0) {
    if (hint !== null) {
      return {level: 'likely', source: hint.source, shown: hint.printed, pick: null, bookOnly: false, groups: []};
    }
    return {...NO_READINGS, groups: []};
  }
  if (groups.length === 1) {
    const pick = tokenReading !== null && groups[0].readings.includes(tokenReading) ? tokenReading : null;
    return {level: 'certain', source: 'dictionary', shown: null, pick, bookOnly: false, groups};
  }

  let pick: string;
  let source: LookupReadings['source'];
  let level: LookupReadings['level'];
  if (choice !== null && has(choice) !== undefined) {
    [pick, source, level] = [choice, 'choice', 'likely'];
  } else if (hint !== null && has(hint.reading) !== undefined) {
    [pick, source, level] = [hint.reading, hint.source, 'likely'];
  } else if (tokenReading !== null && has(tokenReading) !== undefined) {
    [pick, source, level] = [tokenReading, 'tokenizer', blind ? 'guess' : 'likely'];
  } else {
    [pick, source, level] = [groups[0].readings[0], 'dictionary', 'guess'];
  }
  const first = has(pick) ?? groups[0];
  return {level, source, shown: null, pick, bookOnly: false, groups: [first, ...groups.filter(g => g !== first)]};
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
