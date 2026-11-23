/** The CSS Custom Highlight for the open word. Style it with `::highlight(kavita-dict-open)` */
export const OPEN_WORD_HIGHLIGHT = 'kavita-dict-open';

/** The globals the Highlight API lives on, so a spec can pass fakes */
export interface HighlightEnv {
  CSS?: {highlights?: Map<string, unknown>};
  Highlight?: new (...ranges: Range[]) => unknown;
}

function registry(env: HighlightEnv) {
  return typeof env.Highlight === 'function' ? env.CSS?.highlights ?? null : null;
}

/** True when the browser has the CSS Custom Highlight API */
export function supportsHighlights(env: HighlightEnv = globalThis as HighlightEnv): boolean {
  return registry(env) !== null;
}

/** Marks a range as the open word, replacing any word marked before. Does nothing without the API */
export function markOpenWord(range: Range, env: HighlightEnv = globalThis as HighlightEnv): void {
  const highlights = registry(env);
  if (highlights === null || env.Highlight === undefined) return;
  highlights.set(OPEN_WORD_HIGHLIGHT, new env.Highlight(range));
}

/** Removes the open word mark. Does nothing without the API */
export function clearOpenWord(env: HighlightEnv = globalThis as HighlightEnv): void {
  registry(env)?.delete(OPEN_WORD_HIGHLIGHT);
}
