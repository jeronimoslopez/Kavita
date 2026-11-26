import {OPEN_WORD_HIGHLIGHT} from '../highlight';

/**
 * The rule that draws the open word. `::highlight()` matches text in the page, so it can't live in an encapsulated
 * component stylesheet. A highlight can only set colours and decorations, which is why the mark is a tint and an
 * underline
 */
export const OPEN_WORD_CSS = `::highlight(${OPEN_WORD_HIGHLIGHT}) {
  background-color: color-mix(in srgb, var(--primary-color) 28%, transparent);
  text-decoration: underline 3px var(--primary-color);
  text-underline-offset: 3px;
}`;

const STYLE_ID = 'kavita-dict-open-style';
const users = new WeakMap<Document, number>();

/**
 * Adds the open word rule to the document. Each call returns its own remover, and the style element stays until the
 * last remover has run, so two readers attached at once don't take it from each other
 */
export function installOpenWordStyle(doc: Document): () => void {
  const count = users.get(doc) ?? 0;
  users.set(doc, count + 1);
  if (count === 0 && doc.getElementById(STYLE_ID) === null) {
    const style = doc.createElement('style');
    style.id = STYLE_ID;
    style.textContent = OPEN_WORD_CSS;
    doc.head.appendChild(style);
  }

  let removed = false;
  return () => {
    if (removed) return;
    removed = true;
    const left = (users.get(doc) ?? 1) - 1;
    users.set(doc, left);
    if (left === 0) doc.getElementById(STYLE_ID)?.remove();
  };
}
