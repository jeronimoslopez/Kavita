import {DestroyRef, DOCUMENT, inject, Injectable, InjectionToken, Provider} from '@angular/core';
import {filter, Subscription} from 'rxjs';
import {
  DictionaryLookupRequest,
  DictionaryLookupResult,
  DictionaryPagesEvent,
  EpubPageTokens,
} from '../../_models/dictionary/dictionary';
import {DictionaryService} from '../../_services/dictionary.service';
import {EVENTS, MessageHubService} from '../../_services/message-hub.service';
import {enclosingBlock, rangeForBlockOffsets, readBlockText} from './block-text';
import {EpubHit, locateEpubWord} from './epub-locator';
import {clearOpenWord, markOpenWord} from './highlight';
import {carriesSenses, localLookup} from './local-lookup';
import {DictionaryPopupPresenter, PopupCloseReason} from './popup/dictionary-popup.presenter';
import {installOpenWordStyle} from './popup/open-word-style';
import {DictionaryPopupState} from './popup/popup-state';

export type WordLocator = (x: number, y: number, container: Element, page: EpubPageTokens) => EpubHit | null;

export interface WordHighlighter {
  mark(range: Range): void;
  clear(): void;
}

/** Finds the word under a point. A spec replaces it to skip the caret APIs jsdom lacks */
export const DICTIONARY_WORD_LOCATOR = new InjectionToken<WordLocator>('DICTIONARY_WORD_LOCATOR', {
  factory: () => (x, y, container, page) => locateEpubWord(x, y, container, page),
});

/** Marks the open word in the page */
export const DICTIONARY_HIGHLIGHTER = new InjectionToken<WordHighlighter>('DICTIONARY_HIGHLIGHTER', {
  factory: () => ({mark: range => markOpenWord(range), clear: () => clearOpenWord()}),
});

/**
 * How long after a pointer-down that dismissed the popup its click still counts as the dismissal. It covers a slow
 * tap, and is short enough that a later, separate click is never swallowed
 */
export const DISMISS_CLICK_WINDOW_MS = 1000;

export interface ReaderDictionaryOptions {
  /** Read on every open, because a reader can switch writing mode while it's attached */
  vertical: () => boolean;
}

/** One entry on the popup's stack. The first is the tapped word and each part or Part of chip pushes another */
interface StackEntry {
  sub: number | null;
  phrase: number | null;
  /** What the page highlights while the entry shows, the phrase's span for a phrase and the word otherwise */
  range: Range;
  state: DictionaryPopupState;
  /** The groupKey of the reading tab selected on this entry, or null for the first */
  group: string | null;
}

interface OpenWord {
  hit: EpubHit;
  /** The tapped block's text without ruby readings, or null when the hit has no block */
  blockText: string | null;
  /** The word's text without ruby readings */
  surface: string;
  chapterId: number;
  page: number;
  stack: StackEntry[];
  /** DictionaryAnalysisReady arrived for this page while the first lookup asked the server */
  analysisArrived: boolean;
}

/** The pointer-down that closed the popup from outside, and the click it decided about */
interface Dismissal {
  at: number;
  click: MouseEvent | null;
  consumed: boolean;
}

/** Put this in a reader component's providers. Each reader gets its own controller and popup */
export function provideReaderDictionary(): Provider[] {
  return [ReaderDictionaryController, DictionaryPopupPresenter];
}

/**
 * The dictionary popup for one reader. It loads the page's tokens, turns clicks on words into lookups, and keeps the
 * popup's stack of entries. A page whose words carry their senses answers its lookups itself, and a page from an
 * older server sends each one to the server. The reader tells it which page is showing and passes its clicks on. Every click handler
 * in the reader asks consumesClick first, then isLookupClick or handleClick
 */
@Injectable()
export class ReaderDictionaryController {

  private readonly dictionary = inject(DictionaryService);
  private readonly messageHub = inject(MessageHubService);
  private readonly presenter = inject(DictionaryPopupPresenter);
  private readonly locate = inject(DICTIONARY_WORD_LOCATOR);
  private readonly highlighter = inject(DICTIONARY_HIGHLIGHTER);
  private readonly document = inject(DOCUMENT);

  private container: HTMLElement | null = null;
  private vertical: () => boolean = () => false;
  private events: Subscription | null = null;
  private removeStyle: (() => void) | null = null;

  private chapterId: number | null = null;
  private page: number | null = null;
  private tokens: EpubPageTokens | null = null;
  private pageLoad: Subscription | null = null;
  private pageLoading = false;
  /** True after the server answered that the page isn't tokenized yet */
  private waitingForPage = false;
  /** DictionaryPagesReady arrived for this page while its request was out, so the answer may be stale */
  private pageReadyWhileLoading = false;
  /** A reading choice was saved or cleared, so the open word looks up again once the page's data reloads */
  private choiceChanged = false;
  private readonly prioritized = new Set<number>();

  private open: OpenWord | null = null;
  private lookups = new Subscription();
  private dismissal: Dismissal | null = null;
  /** The last click located, so the calls a reader makes for one click run the caret lookup once */
  private lastHit: {event: MouseEvent, hit: EpubHit | null} | null = null;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.detach());
  }

  attach(container: HTMLElement, options: ReaderDictionaryOptions): void {
    this.container = container;
    this.vertical = options.vertical;
    this.lastHit = null;
    this.removeStyle ??= installOpenWordStyle(this.document);
    this.events ??= this.messageHub.messages$.pipe(
      filter(m => m.event === EVENTS.DictionaryPagesReady || m.event === EVENTS.DictionaryAnalysisReady),
    ).subscribe(m => {
      const body = m.payload as DictionaryPagesEvent;
      if (m.event === EVENTS.DictionaryPagesReady) this.onPagesReady(body);
      else this.onAnalysisReady(body);
    });
  }

  detach(): void {
    this.hidePopup();
    this.pageLoad?.unsubscribe();
    this.pageLoad = null;
    this.pageLoading = false;
    this.events?.unsubscribe();
    this.events = null;
    this.removeStyle?.();
    this.removeStyle = null;
    this.container = null;
    this.tokens = null;
    this.chapterId = null;
    this.page = null;
    this.waitingForPage = false;
    this.pageReadyWhileLoading = false;
    this.choiceChanged = false;
    this.dismissal = null;
    this.lastHit = null;
  }

  /** Call when the reader shows a page. Lookups start working once its tokens arrive */
  setPage(chapterId: number, page: number): void {
    this.clearPage();
    this.chapterId = chapterId;
    this.page = page;
    this.loadPage();
  }

  /**
   * Call when the reader replaces the page's HTML. It closes the popup and drops the old page's tokens, so no tap
   * looks up a word against the new DOM until setPage loads the new page's tokens
   */
  clearPage(): void {
    this.hidePopup();
    this.pageLoad?.unsubscribe();
    this.pageLoad = null;
    this.pageLoading = false;
    this.waitingForPage = false;
    this.pageReadyWhileLoading = false;
    this.choiceChanged = false;
    this.chapterId = null;
    this.page = null;
    this.tokens = null;
    this.lastHit = null;
  }

  /** Forgets a pointer-down that closed the popup, so its click no longer counts as the dismissal */
  cancelDismissal(): void {
    this.dismissal = null;
  }

  /** Call when a chapter opens, so the server tokenizes it and the next one first */
  openChapter(chapterId: number): void {
    if (this.prioritized.has(chapterId)) return;
    this.prioritized.add(chapterId);
    this.dictionary.prioritizeChapter(chapterId).subscribe({error: () => this.prioritized.delete(chapterId)});
  }

  /**
   * True for the click that follows a pointer-down which closed the popup from outside, so the reader drops it
   * instead of turning the page or toggling its menu. Call it first in every click handler. A click on another word
   * isn't consumed, so handleClick can open the popup there. Every handler gets the same answer for one click
   */
  consumesClick(event: MouseEvent): boolean {
    const dismissal = this.dismissal;
    if (dismissal === null) return false;
    if (dismissal.click === null) {
      if (Date.now() - dismissal.at > DISMISS_CLICK_WINDOW_MS) {
        this.dismissal = null;
        return false;
      }
      dismissal.click = event;
      dismissal.consumed = this.hitAt(event) === null;
    } else if (dismissal.click !== event) {
      this.dismissal = null;
      return false;
    }
    return dismissal.consumed;
  }

  /** True when the click is on a word the popup can open, so the reader shouldn't turn the page. Opens nothing */
  isLookupClick(event: MouseEvent): boolean {
    return this.hitAt(event) !== null;
  }

  /** Opens the popup for the word under the click. Returns false, and does nothing, when there's no word there */
  handleClick(event: MouseEvent): boolean {
    const hit = this.hitAt(event);
    if (hit === null || this.chapterId === null || this.page === null) return false;
    // A click from the keyboard has no pointer, and reports a detail of 0
    this.openWord(hit, this.chapterId, this.page, event.detail === 0);
    return true;
  }

  isPopupVisible(): boolean {
    return this.open !== null;
  }

  hidePopup(): void {
    this.dismissal = null;
    if (this.open === null) return;
    this.presenter.hide();
    this.closed();
  }

  private hitAt(event: MouseEvent): EpubHit | null {
    if (this.lastHit?.event === event) return this.lastHit.hit;
    const hit = this.container === null || this.tokens === null || this.hasSelection()
      ? null
      : this.locate(event.clientX, event.clientY, this.container, this.tokens);
    this.lastHit = {event, hit};
    return hit;
  }

  private hasSelection(): boolean {
    const selection = this.document.getSelection();
    return selection !== null && !selection.isCollapsed && selection.toString() !== '';
  }

  private loadPage() {
    const chapterId = this.chapterId;
    const page = this.page;
    if (chapterId === null || page === null) return;
    // Unsubscribing drops the answer to a request for a page the reader has left
    this.pageLoad?.unsubscribe();
    this.waitingForPage = false;
    this.pageReadyWhileLoading = false;
    this.pageLoading = true;
    this.pageLoad = this.dictionary.getEpubPage(chapterId, page).subscribe({
      next: tokens => {
        this.pageLoading = false;
        if (tokens !== null) {
          this.tokens = tokens;
          if (this.choiceChanged) this.refreshAfterChoice();
          else this.showArrivedAnalysis();
        }
        if (this.pageReadyWhileLoading) this.loadPage();
        else if (tokens === null) this.waitingForPage = true;
      },
      // Lookups stay off on this page. The reader works the same without them
      error: () => {
        this.pageLoading = false;
      },
    });
  }

  private onPagesReady(body: DictionaryPagesEvent) {
    if (body.chapterId !== this.chapterId || this.page === null || !body.pages.includes(this.page)) return;
    if (this.pageLoading) this.pageReadyWhileLoading = true;
    // Loaded pages reload too, because the event also means the page was tokenized again
    else this.loadPage();
  }

  /**
   * A page that answers its own lookups loads again, which brings the new answers to the open word and to later
   * taps. On a page from an older server the open word asks the server again
   */
  private onAnalysisReady(body: DictionaryPagesEvent) {
    if (body.chapterId !== this.chapterId || this.page === null || !body.pages.includes(this.page)) return;
    const local = this.tokens !== null && carriesSenses(this.tokens);
    if (this.pageLoading) this.pageReadyWhileLoading = true;
    else if (local) this.loadPage();
    if (local) return;

    const open = this.open;
    if (open === null) return;
    const entry = open.stack[0];
    if (entry.state.kind === 'loading') open.analysisArrived = true;
    else if (entry.state.kind === 'result' && entry.state.result.analysis === null) this.fetch(open, entry, true);
  }

  private openWord(hit: EpubHit, chapterId: number, page: number, viaKeyboard: boolean) {
    this.lookups.unsubscribe();
    this.lookups = new Subscription();
    this.dismissal = null;
    const blockText = blockTextOf(hit);
    const open: OpenWord = {
      hit, blockText, surface: blockText?.slice(hit.s, hit.e) ?? '', chapterId, page,
      stack: [{sub: null, phrase: null, range: hit.range, state: {kind: 'loading'}, group: null}],
      analysisArrived: false,
    };
    this.open = open;
    this.highlighter.mark(hit.range);
    this.presenter.show(hit.range, {vertical: this.vertical(), viaKeyboard}, {
      part: sub => this.openPart(sub),
      phrase: index => this.openPhrase(index),
      group: key => this.selectGroup(key),
      chooseReading: reading => this.saveChoice(reading),
      clearReading: () => this.saveChoice(null),
      back: () => this.back(),
      closed: reason => this.closedByPopup(reason),
    });
    // The presenter closes straight away when the word is already off screen
    if (this.open !== open) return;
    this.lookUp(open, open.stack[0]);
  }

  private openPart(sub: number) {
    const open = this.open;
    if (open === null || open.stack[open.stack.length - 1].sub === sub) return;
    const entry: StackEntry = {sub, phrase: null, range: open.hit.range, state: {kind: 'loading'}, group: null};
    this.push(open, entry);
  }

  private openPhrase(index: number) {
    const open = this.open;
    if (open === null) return;
    const top = open.stack[open.stack.length - 1];
    if (top.phrase === index || top.state.kind !== 'result') return;
    const phrase = top.state.result.partOf?.find(p => p.index === index);
    if (phrase === undefined) return;
    const range = phraseRange(open.hit, phrase.s, phrase.e) ?? open.hit.range;
    this.push(open, {sub: null, phrase: index, range, state: {kind: 'loading'}, group: null});
  }

  private selectGroup(key: string) {
    const open = this.open;
    if (open === null) return;
    const top = open.stack[open.stack.length - 1];
    if (top.group === key) return;
    top.group = key;
    this.render();
  }

  /**
   * Saves the reading for the tapped word, or clears the saved one when `reading` is null, and then reloads the
   * page's data so the open word and later taps use it. The request isn't cancelled when the popup closes
   */
  private saveChoice(reading: string | null) {
    const open = this.open;
    const seriesId = this.tokens?.seriesId;
    if (open === null || seriesId === undefined || open.stack.length !== 1) return;
    const state = open.stack[0].state;
    if (state.kind !== 'result') return;
    const wordId = state.result.word.id;
    const {chapterId, page} = open;
    const request = reading === null
      ? this.dictionary.clearReadingChoice(seriesId, wordId)
      : this.dictionary.saveReadingChoice({seriesId, wordId, reading});
    request.subscribe({
      next: () => {
        if (this.chapterId !== chapterId || this.page !== page) return;
        this.choiceChanged = true;
        this.loadPage();
      },
      // The popup keeps showing the reading it had
      error: () => {},
    });
  }

  private push(open: OpenWord, entry: StackEntry) {
    const below = open.stack[open.stack.length - 1];
    open.stack.push(entry);
    if (entry.range !== below.range) this.highlighter.mark(entry.range);
    this.lookUp(open, entry);
  }

  private back() {
    const open = this.open;
    if (open === null || open.stack.length < 2) return;
    const popped = open.stack[open.stack.length - 1];
    open.stack.pop();
    const entry = open.stack[open.stack.length - 1];
    if (entry.range !== popped.range) this.highlighter.mark(entry.range);
    this.render();
    if (entry.state.kind === 'failed') this.lookUp(open, entry);
  }

  /**
   * Answers an entry from the page's tokens when they can, so it shows without a loading state, and asks the server
   * otherwise
   */
  private lookUp(open: OpenWord, entry: StackEntry) {
    const local = this.lookUpLocally(open, entry);
    if (local === undefined) {
      entry.state = {kind: 'loading'};
      this.render();
      this.fetch(open, entry, false);
      return;
    }
    entry.state = local === null ? {kind: 'empty'} : {kind: 'result', result: local};
    this.render();
  }

  /** @returns Undefined when the page's tokens can't answer, because they're from an older server */
  private lookUpLocally(open: OpenWord, entry: StackEntry): DictionaryLookupResult | null | undefined {
    const tokens = this.tokens;
    if (tokens === null || open.blockText === null || !carriesSenses(tokens)) return undefined;
    return localLookup(tokens, {block: open.hit.blockIndex, s: open.hit.s, sub: entry.sub, phrase: entry.phrase},
      open.blockText);
  }

  /**
   * Puts an analysis that came with reloaded tokens on the open word, the way a silent fetch does for an older
   * server. A result that still has no analysis leaves the entry as it is
   */
  private showArrivedAnalysis() {
    const open = this.open;
    if (open === null) return;
    const entry = open.stack[0];
    if (entry.state.kind !== 'result' || entry.state.result.analysis !== null) return;
    const result = this.lookUpLocally(open, entry);
    if (result === undefined || result === null || result.analysis === null) return;
    entry.state = {kind: 'result', result};
    this.renderIfShowing(open, entry);
  }

  /**
   * Looks the open word up again from the reloaded page, keeping its selected tab, so the popup shows the saved
   * choice without closing. A page from an older server asks the server again
   */
  private refreshAfterChoice() {
    this.choiceChanged = false;
    const open = this.open;
    if (open === null) return;
    const entry = open.stack[0];
    if (entry.state.kind !== 'result') return;
    const result = this.lookUpLocally(open, entry);
    if (result === undefined) {
      this.fetch(open, entry, true);
      return;
    }
    if (result === null) return;
    entry.state = {kind: 'result', result};
    this.renderIfShowing(open, entry);
  }

  /**
   * Asks the server about one stack entry and stores the answer on it. A silent fetch refreshes an entry already showing a result,
   * so a failure or an empty answer leaves that result in place
   */
  private fetch(open: OpenWord, entry: StackEntry, silent: boolean) {
    const request: DictionaryLookupRequest = {
      chapterId: open.chapterId, page: open.page, block: open.hit.blockIndex, s: open.hit.s, sub: entry.sub,
      phrase: entry.phrase,
    };
    this.lookups.add(this.dictionary.lookup(request).subscribe({
      next: result => {
        if (silent && result === null) return;
        entry.state = result === null ? {kind: 'empty'} : {kind: 'result', result};
        this.renderIfShowing(open, entry);
        if (entry === open.stack[0] && open.analysisArrived) {
          open.analysisArrived = false;
          // The answer may predate the analysis the event announced
          if (result !== null && result.analysis === null) this.fetch(open, entry, true);
        }
      },
      error: () => {
        if (silent) return;
        entry.state = {kind: 'failed'};
        this.renderIfShowing(open, entry);
      },
    }));
  }

  private renderIfShowing(open: OpenWord, entry: StackEntry) {
    if (this.open === open && open.stack[open.stack.length - 1] === entry) this.render();
  }

  private render() {
    const open = this.open;
    if (open === null) return;
    const top = open.stack[open.stack.length - 1];
    this.presenter.render({
      state: top.state,
      surface: open.surface,
      canGoBack: open.stack.length > 1,
      openSub: top.sub,
      openPhrase: top.phrase,
      openGroup: top.group,
      canChoose: top === open.stack[0] && this.tokens?.seriesId !== undefined,
    });
  }

  private closedByPopup(reason: PopupCloseReason) {
    if (reason === 'outside') this.dismissal = {at: Date.now(), click: null, consumed: false};
    this.closed();
  }

  private closed() {
    this.open = null;
    this.lookups.unsubscribe();
    this.lookups = new Subscription();
    this.highlighter.clear();
  }
}

/** The block's text, which leaves out ruby readings that the hit range's own text includes */
function blockTextOf(hit: EpubHit): string | null {
  const block = enclosingBlock(hit.range.startContainer);
  return block === null ? null : readBlockText(block).text;
}

/** A Range over block offsets `[s, e)` in the hit's block, or null when the block's text doesn't reach them */
function phraseRange(hit: EpubHit, s: number, e: number): Range | null {
  const block = enclosingBlock(hit.range.startContainer);
  return block === null ? null : rangeForBlockOffsets(readBlockText(block), s, e);
}
