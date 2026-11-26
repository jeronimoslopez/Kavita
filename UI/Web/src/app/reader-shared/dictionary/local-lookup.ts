import {
  DictionaryLookupResult,
  DictionaryPart,
  DictionaryPhrase,
  DictionaryWord,
  EpubPageTokens,
  EpubPageWord,
  SubToken,
} from '../../_models/dictionary/dictionary';

/** A tap, as the `block`, `s`, `sub` and `phrase` of a DictionaryLookupRequest */
export interface LocalLookupRequest {
  block: number;
  s: number;
  sub: number | null;
  phrase?: number | null;
}

/**
 * True when the page's words carry their senses, so localLookup can answer its taps. An older server sends words
 * without them, and the reader asks the server instead. A page with no words answers every tap with null either way
 */
export function carriesSenses(page: EpubPageTokens): boolean {
  const first = Object.values(page.words)[0];
  return first === undefined || Array.isArray(first.senses);
}

/**
 * The answer `POST api/dictionary/lookup` gives for a tap, built from the page response. It follows LookupAsync and
 * LookupPhraseAsync in the server's DictionaryService rule for rule, and lookup-parity.json holds the server's answers
 * it's checked against.
 *
 * @param blockText The text of the tapped block, rebuilt from the DOM with readBlockText. The locator only reports
 * hits in blocks whose rebuilt length matches the stored one
 * @returns Null where the server answers 204
 */
export function localLookup(page: EpubPageTokens, request: LocalLookupRequest,
                            blockText: string): DictionaryLookupResult | null {
  const block = page.blocks.find(b => b.index === request.block);
  if (block === undefined) return null;
  const phrases = block.phrases ?? [];

  if (request.phrase !== null && request.phrase !== undefined) {
    if (request.phrase < 0 || request.phrase >= phrases.length) return null;
    const [s, e, w] = phrases[request.phrase];
    if (request.s < s || request.s >= e) return null;
    const word = wordOf(page, w);
    if (word === null) return null;
    return {
      word: dictionaryWord(w, word, slice(blockText, s, e), null),
      senses: word.senses ?? [],
      analysis: null,
      parts: [],
      partOf: [],
    };
  }

  const token = block.tokens.find(t => t[0] === request.s);
  if (token === undefined) return null;
  const [tokenS, tokenE, tokenW, tokenR] = token;
  const parts = token[4] ?? [];

  let target: SubToken;
  if (request.sub === null) {
    target = [tokenS, tokenE, tokenW, tokenR];
  } else {
    if (request.sub < 0 || request.sub >= parts.length) return null;
    target = parts[request.sub];
  }

  const [targetS, targetE, targetW, targetR] = target;
  if (targetW === null) return null;
  const word = wordOf(page, targetW);
  if (word === null) return null;

  let analysis: DictionaryLookupResult['analysis'] = null;
  if (request.sub === null) {
    const entry = page.analysis.find(a => a.block === block.index && a.s === tokenS);
    if (entry !== undefined) {
      analysis = {senseKey: entry.senseKey, reading: entry.reading, generator: entry.generator ?? ''};
    }
  }

  const partResults: DictionaryPart[] = [];
  parts.forEach(([s, e, w], index) => {
    if (w === null) return;
    const partWord = wordOf(page, w);
    if (partWord === null) return;
    partResults.push({
      sub: index, wordId: w, surface: slice(blockText, s, e), lemma: partWord.lemma, reading: partWord.reading,
      senses: partWord.senses ?? [],
    });
  });

  const partOf: DictionaryPhrase[] = [];
  phrases.forEach(([s, e, w], index) => {
    if (!(s <= tokenS && tokenE <= e)) return;
    const phraseWord = wordOf(page, w);
    if (phraseWord === null) return;
    partOf.push({index, s, e, lemma: phraseWord.lemma, reading: phraseWord.reading, gloss: firstGloss(phraseWord)});
  });

  return {
    word: dictionaryWord(targetW, word, slice(blockText, targetS, targetE), targetR),
    senses: word.senses ?? [],
    analysis,
    parts: partResults,
    partOf,
  };
}

function wordOf(page: EpubPageTokens, id: number): EpubPageWord | null {
  return Object.hasOwn(page.words, id) ? page.words[id] : null;
}

function dictionaryWord(id: number, word: EpubPageWord, surface: string,
                        occurrenceReading: string | null): DictionaryWord {
  return {
    id, surface, lemma: word.lemma, pos: word.pos ?? '', isProper: word.isProper ?? false, occurrenceReading,
    reading: word.reading, readings: word.readings ?? [], jlpt: word.jlpt,
  };
}

/** The first sense's glosses joined with "; ", the way the popup shows a sense, or empty when there are no senses */
function firstGloss(word: EpubPageWord): string {
  const senses = word.senses ?? [];
  return senses.length === 0 ? '' : senses[0].gloss.join('; ');
}

/** The server's Slice, which clamps the offsets to the text where String.slice would count from the end */
function slice(text: string, s: number, e: number): string {
  const start = Math.min(Math.max(s, 0), text.length);
  const end = Math.min(Math.max(e, start), text.length);
  return text.slice(start, end);
}
