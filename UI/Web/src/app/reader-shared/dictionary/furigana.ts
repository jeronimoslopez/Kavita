export interface FuriSegment {
  text: string;
  ruby: string | null;
}

// ヵ and ヶ sit in the katakana block but are read like kanji (一ヶ月)
function isKana(ch: string): boolean {
  const c = ch.codePointAt(0) ?? 0;
  if (c === 0x30f5 || c === 0x30f6) return false;
  return (c >= 0x3041 && c <= 0x3096) || (c >= 0x30a1 && c <= 0x30fa) || c === 0x30fc;
}

/** Katakana converted to hiragana, with everything else left as it is */
export function toHiragana(s: string): string {
  let out = '';
  for (const ch of s) {
    const c = ch.codePointAt(0) ?? 0;
    out += c >= 0x30a1 && c <= 0x30f6 ? String.fromCodePoint(c - 0x60) : ch;
  }
  return out;
}

function escapeRegExp(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/**
 * Splits a surface into kana and kanji runs and gives each kanji run its part of the reading.
 * Falls back to one ruby over the whole surface when the reading can't be aligned
 */
export function furigana_segments(surface: string, reading: string | null): FuriSegment[] {
  const s = Array.from(surface);
  if (!reading || s.every(isKana)) return [{text: surface, ruby: null}];
  const whole: FuriSegment[] = [{text: surface, ruby: reading}];
  const r = Array.from(toHiragana(reading));

  let p = 0;
  while (p < s.length && p < r.length && isKana(s[p]) && toHiragana(s[p]) === r[p]) {
    p++;
  }
  let q = 0;
  while (
    q < s.length - p &&
    q < r.length - p &&
    isKana(s[s.length - 1 - q]) &&
    toHiragana(s[s.length - 1 - q]) === r[r.length - 1 - q]
  ) {
    q++;
  }
  const midSurface = s.slice(p, s.length - q);
  const midReading = r.slice(p, r.length - q).join('');
  if (midSurface.length === 0 || midReading.length === 0) return whole;

  const runs: {text: string, kana: boolean}[] = [];
  for (const ch of midSurface) {
    const kana = isKana(ch);
    const last = runs[runs.length - 1];
    if (last && last.kana === kana) last.text += ch;
    else runs.push({text: ch, kana});
  }
  const pattern = runs
    .map(run => (run.kana ? escapeRegExp(toHiragana(run.text)) : '(.+?)'))
    .join('');
  const match = new RegExp(`^${pattern}$`, 'u').exec(midReading);
  if (!match) return whole;

  const out: FuriSegment[] = [];
  const pushKana = (text: string) => {
    if (text) out.push({text, ruby: null});
  };
  pushKana(s.slice(0, p).join(''));
  let group = 1;
  for (const run of runs) {
    if (run.kana) pushKana(run.text);
    else out.push({text: run.text, ruby: match[group++] ?? ''});
  }
  pushKana(s.slice(s.length - q).join(''));
  return out;
}
