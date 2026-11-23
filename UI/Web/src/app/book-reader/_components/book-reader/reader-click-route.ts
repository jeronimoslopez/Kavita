/** What one click on the book reader's content is about, as the reader sees it before any handler runs */
export interface ReaderClick {
  /** The built-in dictionary is on for this reader, which turns Yomitan scanning off */
  dictionary: boolean;
  /**
   * The dictionary consumes this click, because its pointer-down closed the popup from outside. A click like that on
   * another word isn't consumed, so it can open that word
   */
  dismissedPopup: boolean;
  /** The dictionary can open a word at the click. With the dictionary off, Yomitan can */
  onWord: boolean;
  /** The click is inside a link or another element with its own click action */
  onLink: boolean;
  hasSelection: boolean;
  /** The click is inside an annotation highlight */
  onHighlight: boolean;
}

/**
 * The one handler a click goes to. `drop` swallows it. `dictionary` opens the popup and `yomitan` leaves the click to
 * Yomitan. `link` and `annotation` leave it to the link's or the highlight's own handler, and `reader` to the
 * reader's page turn and menu toggle
 */
export type ReaderClickRoute = 'drop' | 'dictionary' | 'yomitan' | 'link' | 'annotation' | 'reader';

/**
 * Picks the handler for a click. A tap on a word inside a highlight opens the dictionary and not the annotation,
 * because a highlight can cover a whole paragraph and its words would otherwise never be looked up. The annotation
 * stays reachable from the annotations drawer and from a tap on a part of the highlight that isn't a word. With the
 * dictionary off, Yomitan skips highlights and links, so their handlers run as before
 */
export function routeReaderClick(click: ReaderClick): ReaderClickRoute {
  if (click.dictionary && click.dismissedPopup) return 'drop';
  if (click.onLink) return 'link';
  if (click.hasSelection) return 'reader';
  if (click.dictionary && click.onWord) return 'dictionary';
  if (click.onHighlight) return 'annotation';
  if (!click.dictionary && click.onWord) return 'yomitan';
  return 'reader';
}
