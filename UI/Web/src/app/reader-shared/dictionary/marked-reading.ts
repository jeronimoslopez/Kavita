/** A twin of Kavita.Services/Dictionary/MarkedReading.cs. marked-reading.spec.ts ports its tests case for case */

/** One ruby span of a block, `[s, e, reading]`, as EpubPageBlock.ruby holds it */
export type RubySpan = [s: number, e: number, reading: string];

/**
 * The reading a book's furigana gives one token. `printed` is the surface's reading as the book prints it, each covered
 * base replaced by its ruby. `dictionaryPrinted` is the reading of the token's lemma in the book's script, or null
 * when the surface's stem doesn't match the lemma's. The two readings are the printed forms in hiragana
 */
export interface MarkedReadingResult {
  printed: string;
  reading: string;
  dictionaryPrinted: string | null;
  dictionaryReading: string | null;
}

/**
 * The token's reading from the ruby spans over it, when the book marks every kanji of the token. A token is marked
 * when at least one span lies inside it, every kanji of its surface is under a span, and no span crosses its start or
 * its end. A span whose reading isn't all kana counts as no span, and overlapping spans leave the token unmarked.
 *
 * @param text The block's text
 * @param s Start of the top-level token, a UTF-16 offset into `text`
 * @param e End of the token
 * @param ruby The block's ruby spans, in any order
 * @param lemma The lemma of the token's word, or null when it has none
 * @returns Null when the token isn't marked
 */
export function markedReadingOf(text: string, s: number, e: number, ruby: readonly RubySpan[],
                                lemma: string | null): MarkedReadingResult | null {
  if (s < 0 || e > text.length || e <= s) return null;

  const inside: RubySpan[] = [];
  for (const span of ruby) {
    if (span[1] <= s || span[0] >= e || !isKanaReading(span[2])) continue;
    if (span[0] < s || span[1] > e) return null;
    inside.push(span);
  }
  if (inside.length === 0) return null;
  inside.sort((a, b) => a[0] - b[0]);

  let printed = '';
  let position = s;
  let next = 0;
  while (position < e) {
    if (next < inside.length && inside[next][0] < position) return null;
    if (next < inside.length && inside[next][0] === position) {
      printed += inside[next][2];
      position = inside[next][1];
      next++;
      continue;
    }

    const length = position + 1 < e && isSurrogatePair(text, position) ? 2 : 1;
    const codePoint = length === 2 ? text.codePointAt(position)! : text.charCodeAt(position);
    if (isKanji(codePoint)) return null;
    printed += text.slice(position, position + length);
    position += length;
  }

  const dictionary = dictionaryForm(text.slice(s, e), printed, inside[inside.length - 1][1] - s, lemma);
  return {
    printed, reading: toHiraganaServer(printed), dictionaryPrinted: dictionary,
    dictionaryReading: dictionary === null ? null : toHiraganaServer(dictionary),
  };
}

/**
 * The reading of the lemma, made by swapping the kana after the surface's stem for the lemma's, so 開か read あか with
 * the lemma 開く gives あく. The stem is the surface without its trailing kana from `coveredEnd` on. It must be the
 * lemma's stem character for character, and not empty, or the result is null
 *
 * @param coveredEnd An offset into `surface` the stem never ends before. MarkedReading passes where the last ruby span
 * ends, because kana under a span belong to the stem. 0 lets every trailing kana go
 */
export function dictionaryForm(surface: string, printed: string, coveredEnd: number,
                               lemma: string | null): string | null {
  if (lemma === null || lemma === '') return null;
  if (lemma === surface) return printed;

  let surfaceStem = surface.length;
  while (surfaceStem > coveredEnd && isKana(surface.charCodeAt(surfaceStem - 1))) surfaceStem--;
  let lemmaStem = lemma.length;
  while (lemmaStem > 0 && isKana(lemma.charCodeAt(lemmaStem - 1))) lemmaStem--;

  if (surfaceStem === 0 || surface.slice(0, surfaceStem) !== lemma.slice(0, lemmaStem)) return null;

  // The trailing kana aren't under a span, so the printed reading ends with them as written
  return printed.slice(0, Math.max(0, printed.length - (surface.length - surfaceStem))) + lemma.slice(lemmaStem);
}

/**
 * Katakana turned into the hiragana for the same sound, the way the server's MarkedReading.ToHiragana does it, with ヵ
 * and ヶ, ヽ and ヾ mapped too. The long vowel mark and other characters stay as they are. furigana.ts has its own
 * toHiragana for display, which leaves ヵ and ヶ alone
 */
export function toHiraganaServer(value: string): string {
  let result = '';
  for (let i = 0; i < value.length; i++) {
    const c = value.charCodeAt(i);
    if (c >= 0x30a1 && c <= 0x30f6) result += String.fromCharCode(c - 0x60);
    else if (c === 0x30fd) result += 'ゝ';
    else if (c === 0x30fe) result += 'ゞ';
    else result += value[i];
  }
  return result;
}

export function isKanji(codePoint: number): boolean {
  return (codePoint >= 0x3400 && codePoint <= 0x4dbf) || (codePoint >= 0x4e00 && codePoint <= 0x9fff)
    || (codePoint >= 0xf900 && codePoint <= 0xfaff) || (codePoint >= 0x20000 && codePoint <= 0x323af)
    || codePoint === 0x3005 || codePoint === 0x3006 || codePoint === 0x3007;
}

/** Hiragana, katakana and the long vowel mark, without the katakana middle dot, for one UTF-16 unit */
function isKana(c: number): boolean {
  return (c >= 0x3041 && c <= 0x3096) || (c >= 0x3099 && c <= 0x309f) || (c >= 0x30a1 && c <= 0x30fa)
    || (c >= 0x30fc && c <= 0x30ff) || (c >= 0x31f0 && c <= 0x31ff);
}

function isKanaReading(reading: string): boolean {
  if (reading.length === 0) return false;
  for (let i = 0; i < reading.length; i++) {
    if (!isKana(reading.charCodeAt(i))) return false;
  }
  return true;
}

function isSurrogatePair(text: string, i: number): boolean {
  const high = text.charCodeAt(i);
  const low = text.charCodeAt(i + 1);
  return high >= 0xd800 && high <= 0xdbff && low >= 0xdc00 && low <= 0xdfff;
}
