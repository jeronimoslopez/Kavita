/** Mirrors of Kavita.Models/DTOs/Dictionary, the contract behind api/dictionary */

/**
 * One short-unit part of a compound, `[s, e, w, r]`. Offsets are UTF-16 offsets into the block's text,
 * `w` is a word id or null, and `r` is the occurrence's hiragana reading or null
 */
export type SubToken = [s: number, e: number, w: number | null, r: string | null];

/** One token, `[s, e, w, r, sub]`, where `sub` holds a compound's parts or is null */
export type Token = [s: number, e: number, w: number | null, r: string | null, sub: SubToken[] | null];

/** A dictionary expression over two or more whole tokens, `[s, e, w]`, where `w` is always a word id */
export type Phrase = [s: number, e: number, w: number];

export interface Sense {
  /** `<seq>:<n>`, the JMdict entry and the sense's 1-based position in it */
  key: string;
  pos: string[];
  gloss: string[];
  /** The readings of its entry the sense is restricted to. Missing when it applies to every reading */
  r?: string[];
}

/** One JMdict entry of a word, in the order the word's senses appear */
export interface WordEntry {
  seq: number;
  common: boolean;
  /** Hiragana */
  readings: string[];
}

/** Where the reading of a word's first reading group came from */
export type ReadingSource = 'furigana' | 'book' | 'series' | 'choice' | 'tokenizer' | 'dictionary';

/** How sure the first reading group is */
export type ReadingLevel = 'certain' | 'likely' | 'guess';

/** The reading the book or its series marks for a word */
export interface ReadingHint {
  /** Hiragana */
  reading: string;
  /** As the book prints it */
  printed: string;
  source: 'book' | 'series';
}

/**
 * One JMdict entry, or the part of one whose senses apply to the same readings, with every such part that has the
 * same readings list folded into the first. A word whose lemma has no kanji has one group holding all its senses
 */
export interface ReadingGroup {
  /** The group's first entry */
  seq: number;
  /** In entry order. The lemma in hiragana alone for a word whose lemma has no kanji */
  readings: string[];
  /** True when any entry in the group is */
  common: boolean;
  senses: Sense[];
}

/** The reading groups of a lookup, with the one that fits the occurrence first */
export interface LookupReadings {
  level: ReadingLevel;
  source: ReadingSource;
  /** A reading to show in the headword in place of the first group's, or null */
  shown: string | null;
  /** The reading, in hiragana, that put the first group first, or null when nothing picked one */
  pick: string | null;
  /** The book marks a reading no group has, so the popup lists every sense ungrouped */
  bookOnly: boolean;
  groups: ReadingGroup[];
}

export interface EpubPageBlock {
  /** The block's `data-kb` value */
  index: number;
  /** UTF-16 length of the block's text. When the text rebuilt from the DOM has another length, skip the block */
  length: number;
  tokens: Token[];
  /** In stored order, so a phrase's position is the `phrase` index that looks it up. Missing when the block has none */
  phrases?: Phrase[];
  /**
   * The block's furigana, `[s, e, reading]` with UTF-16 offsets of the base into the block's text and the `rt` text as
   * the book prints it. A span of a `ruby` element with two or more spans has a fourth item, the same number for each
   * span of that element. Missing when the block has none
   */
  ruby?: [s: number, e: number, reading: string, group?: number][];
  /** Start offsets of the top-level tokens whose reading the tokenizer chose by fixed cost. Missing when there are none */
  blind?: number[];
}

/**
 * A word of the page. The fields after `jlpt` are what a lookup needs, and an older server leaves them out, so the
 * reader asks the server for each tap instead
 */
export interface EpubPageWord {
  lemma: string;
  reading: string;
  /** 1 (N1) to 5 (N5), or null when the word isn't listed */
  jlpt: number | null;
  pos?: string;
  /** Missing when false */
  isProper?: boolean;
  readings?: string[];
  senses?: Sense[];
  /** The word's JMdict entries. Version 2 data leaves them out */
  entries?: WordEntry[];
  hint?: ReadingHint;
  /** The reading the user saved for this word in this series */
  choice?: string;
}

export interface AnalysisEntry {
  block: number;
  s: number;
  /** Null when none of the offered senses fit */
  senseKey: string | null;
  reading: string | null;
  /** The model that answered. An older server leaves it out */
  generator?: string;
}

export interface EpubPageTokens {
  /** Changes when the page is tokenized again */
  version: string;
  /** The chapter's series. An older server leaves it out */
  seriesId?: number;
  blocks: EpubPageBlock[];
  /** Every word a lookup on the page can return, keyed by word id. JSON keys are strings */
  words: Record<string, EpubPageWord>;
  analysis: AnalysisEntry[];
}

export interface DictionaryLookupRequest {
  chapterId: number;
  /** 0-based, as the book reader counts pages */
  page: number;
  block: number;
  /** Start offset of the tapped top-level token */
  s: number;
  /** Index into the token's parts, or null for the whole token */
  sub: number | null;
  /** A `DictionaryPhrase.index` to look that phrase up instead, with `sub` null. Null or missing for the word */
  phrase?: number | null;
}

export interface DictionaryWord {
  id: number;
  surface: string;
  lemma: string;
  pos: string;
  isProper: boolean;
  /** The tokenizer's reading of this occurrence */
  occurrenceReading: string | null;
  /** The dictionary's usual reading */
  reading: string;
  readings: string[];
  jlpt: number | null;
}

export interface DictionaryAnalysis {
  senseKey: string | null;
  reading: string | null;
  /** The model that answered, for the AI disclosure */
  generator: string;
}

export interface DictionaryPart {
  /** Send back as `sub` to look the part up */
  sub: number;
  wordId: number;
  surface: string;
  lemma: string;
  reading: string;
  senses: Sense[];
}

/** A dictionary expression that contains the tapped word, such as 気がする around 気 */
export interface DictionaryPhrase {
  /** Send back as `phrase` to look the expression up */
  index: number;
  /** UTF-16 offsets of the expression in the block's text, like a token's */
  s: number;
  e: number;
  lemma: string;
  reading: string;
  /** The phrase's first sense as short text, or empty */
  gloss: string;
}

export interface DictionaryLookupResult {
  word: DictionaryWord;
  senses: Sense[];
  /** Always null for a part of a compound and for a phrase */
  analysis: DictionaryAnalysis | null;
  /** The top-level token's parts, also while one part is open */
  parts: DictionaryPart[];
  /** Expressions the word is part of. An older server leaves it out, and a phrase's own result has none */
  partOf?: DictionaryPhrase[];
  /** The word's reading groups and which fits this occurrence. An older server leaves it out */
  readings?: LookupReadings;
}

export interface ReadingChoiceRequest {
  seriesId: number;
  wordId: number;
  reading: string;
}

export interface AnalysisItem {
  id: number;
  /** Offsets into the passage's text */
  s: number;
  e: number;
  surface: string;
  lemma: string;
  readings: string[];
  senses: Sense[];
}

export interface AnalysisPassage {
  text: string;
  items: AnalysisItem[];
}

export interface AnalysisAnswer {
  id: number;
  sense: string | null;
  reading: string | null;
}

export interface AnalysisStoreRequest {
  generator: string;
  promptVersion: string;
  items: AnalysisAnswer[];
}

export interface AnalysisRejection {
  id: number;
  reason: string;
}

export interface AnalysisStoreResult {
  stored: number;
  rejected: AnalysisRejection[];
}

/** Body of DictionaryPagesReady and DictionaryAnalysisReady */
export interface DictionaryPagesEvent {
  seriesId: number;
  chapterId: number;
  /** 0-based pages */
  pages: number[];
}
