import {DestroyRef, DOCUMENT, inject, Injectable, InjectionToken, Provider} from '@angular/core';
import {filter, Subscription} from 'rxjs';
import {DictionaryPagesEvent, EpubPageTokens} from '../../_models/dictionary/dictionary';
import {DictionaryService} from '../../_services/dictionary.service';
import {EVENTS, MessageHubService} from '../../_services/message-hub.service';
import {enclosingBlock, readBlockText} from './block-text';
import {EpubHit, locateEpubWord} from './epub-locator';
import {clearOpenWord, markOpenWord} from './highlight';
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

/** One entry on the popup's stack. The first is the tapped word and each part chip pushes another */
interface StackEntry {
  sub: number | null;
  state: DictionaryPopupState;
}

interface OpenWord {
  hit: EpubHit;
  /** The word's text without ruby readings */
  surface: string;
  chapterId: number;
  page: number;
  stack: StackEntry[];
  /** DictionaryAnalysisReady arrived for this page while the first lookup was still running */
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
 * popup's stack of entries. The reader tells it which page is showing and passes its clicks on. Every click handler
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
  private readonly prioritized = new Set<number>();

  private open: OpenWord | null = null;
  private lookups = new Subscription();
  private dismissal: Dismissal | null = null;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.detach());
  }

  attach(container: HTMLElement, options: ReaderDictionaryOptions): void {
    this.container = container;
    this.vertical = options.vertical;
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
    this.dismissal = null;
  }

  /** Call when the reader shows a page. Lookups start working once its tokens arrive */
  setPage(chapterId: number, page: number): void {
    this.hidePopup();
    this.chapterId = chapterId;
    this.page = page;
    this.tokens = null;
    this.loadPage();
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
    if (this.open === null) return;
    this.presenter.hide();
    this.closed();
  }

  private hitAt(event: MouseEvent): EpubHit | null {
    if (this.container === null || this.tokens === null || this.hasSelection()) return null;
    return this.locate(event.clientX, event.clientY, this.container, this.tokens);
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
        if (tokens !== null) this.tokens = tokens;
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

  private onAnalysisReady(body: DictionaryPagesEvent) {
    const open = this.open;
    if (open === null || body.chapterId !== open.chapterId || !body.pages.includes(open.page)) return;
    const entry = open.stack[0];
    if (entry.state.kind === 'loading') open.analysisArrived = true;
    else if (entry.state.kind === 'result' && entry.state.result.analysis === null) this.fetch(open, entry, true);
  }

  private openWord(hit: EpubHit, chapterId: number, page: number, viaKeyboard: boolean) {
    this.lookups.unsubscribe();
    this.lookups = new Subscription();
    this.dismissal = null;
    const open: OpenWord = {
      hit, surface: surfaceOf(hit), chapterId, page, stack: [{sub: null, state: {kind: 'loading'}}],
      analysisArrived: false,
    };
    this.open = open;
    this.highlighter.mark(hit.range);
    this.presenter.show(hit.range, {vertical: this.vertical(), viaKeyboard}, {
      part: sub => this.openPart(sub),
      back: () => this.back(),
      closed: reason => this.closedByPopup(reason),
    });
    // The presenter closes straight away when the word is already off screen
    if (this.open !== open) return;
    this.render();
    this.fetch(open, open.stack[0], false);
  }

  private openPart(sub: number) {
    const open = this.open;
    if (open === null || open.stack[open.stack.length - 1].sub === sub) return;
    const entry: StackEntry = {sub, state: {kind: 'loading'}};
    open.stack.push(entry);
    this.render();
    this.fetch(open, entry, false);
  }

  private back() {
    const open = this.open;
    if (open === null || open.stack.length < 2) return;
    open.stack.pop();
    const entry = open.stack[open.stack.length - 1];
    this.render();
    if (entry.state.kind === 'failed') {
      entry.state = {kind: 'loading'};
      this.render();
      this.fetch(open, entry, false);
    }
  }

  /**
   * Looks up one stack entry and stores the answer on it. A silent fetch refreshes an entry already showing a result,
   * so a failure or an empty answer leaves that result in place
   */
  private fetch(open: OpenWord, entry: StackEntry, silent: boolean) {
    const request = {chapterId: open.chapterId, page: open.page, block: open.hit.blockIndex, s: open.hit.s, sub: entry.sub};
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

/** The word as it reads in the block, which leaves out ruby readings that the range's own text includes */
function surfaceOf(hit: EpubHit): string {
  const block = enclosingBlock(hit.range.startContainer);
  return block === null ? '' : readBlockText(block).text.slice(hit.s, hit.e);
}
