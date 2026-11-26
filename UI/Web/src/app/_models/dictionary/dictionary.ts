/** Mirrors of Kavita.Models/DTOs/Dictionary, the contract behind api/dictionary */

/**
 * One short-unit part of a compound, `[s, e, w, r]`. Offsets are UTF-16 offsets into the block's text,
 * `w` is a word id or null, and `r` is the occurrence's hiragana reading or null
 */
export type SubToken = [s: number, e: number, w: number | null, r: string | null];

/** One token, `[s, e, w, r, sub]`, where `sub` holds a compound's parts or is null */
export type Token = [s: number, e: number, w: number | null, r: string | null, sub: SubToken[] | null];

export interface Sense {
  /** `<seq>:<n>`, the JMdict entry and the sense's 1-based position in it */
  key: string;
  pos: string[];
  gloss: string[];
}

export interface EpubPageBlock {
  /** The block's `data-kb` value */
  index: number;
  /** UTF-16 length of the block's text. When the text rebuilt from the DOM has another length, skip the block */
  length: number;
  tokens: Token[];
}

export interface WordBrief {
  lemma: string;
  reading: string;
  /** 1 (N1) to 5 (N5), or null when the word isn't listed */
  jlpt: number | null;
}

export interface AnalysisEntry {
  block: number;
  s: number;
  /** Null when none of the offered senses fit */
  senseKey: string | null;
  reading: string | null;
}

export interface EpubPageTokens {
  /** Changes when the page is tokenized again */
  version: string;
  blocks: EpubPageBlock[];
  /** Keyed by word id. JSON keys are strings */
  words: Record<string, WordBrief>;
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
