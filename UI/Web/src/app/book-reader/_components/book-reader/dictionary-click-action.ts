/** What the built-in dictionary needs to know about one click in the book reader */
export interface DictionaryClick {
  /**
   * The click lands on the page: inside the book content or on a page-turn strip. The reader's own controls, such as
   * the bottom action bar and the image bookmark icons, are not on the page
   */
  onPage: boolean;
  /**
   * The dictionary consumes this click, because its pointer-down closed the popup from outside. A click like that on
   * another word isn't consumed, so it can open that word
   */
  dismissedPopup: boolean;
  /** The dictionary can open a word at the click. Links, selections and other controls never count as words */
  onWord: boolean;
}

/**
 * What the reader does with a click while the built-in dictionary is on. `release` lets a click on a control through
 * and forgets any pending dismissal. `lookup` opens the popup. `drop` swallows the click that closed the popup.
 * `pass` leaves the click to the link, highlight or reader handler under it
 */
export type DictionaryClickAction = 'release' | 'lookup' | 'drop' | 'pass';

/**
 * A word wins over a highlight, because a highlight can cover a whole paragraph and its words would otherwise never
 * be looked up. A tap on the rest of the highlight still passes to its handler and opens the annotation
 */
export function dictionaryClickAction(click: DictionaryClick): DictionaryClickAction {
  if (!click.onPage) return 'release';
  if (click.onWord) return 'lookup';
  if (click.dismissedPopup) return 'drop';
  return 'pass';
}
