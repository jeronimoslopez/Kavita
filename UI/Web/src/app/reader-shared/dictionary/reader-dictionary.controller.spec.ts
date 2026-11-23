import {TestBed} from '@angular/core/testing';
import {of, Subject} from 'rxjs';
import {
  DictionaryLookupRequest,
  DictionaryLookupResult,
  DictionaryPagesEvent,
  EpubPageTokens,
} from '../../_models/dictionary/dictionary';
import {DictionaryService} from '../../_services/dictionary.service';
import {EVENTS, Message, MessageHubService} from '../../_services/message-hub.service';
import {EpubHit} from './epub-locator';
import {DictionaryPopupPresenter, PopupCloseReason, PopupHandlers} from './popup/dictionary-popup.presenter';
import {DictionaryPopupView} from './popup/popup-state';
import {
  DICTIONARY_HIGHLIGHTER,
  DICTIONARY_WORD_LOCATOR,
  DISMISS_CLICK_WINDOW_MS,
  ReaderDictionaryController,
} from './reader-dictionary.controller';

const TOKENS: EpubPageTokens = {version: 'v1', blocks: [], words: {}, analysis: []};

function lookupResult(lemma: string, analysis: DictionaryLookupResult['analysis'] = null): DictionaryLookupResult {
  return {
    word: {id: 1, surface: lemma, lemma, pos: 'n', isProper: false, occurrenceReading: null, reading: 'よみ', readings: [], jlpt: null},
    senses: [{key: '1:1', pos: ['n'], gloss: [lemma]}],
    analysis,
    parts: [{sub: 0, wordId: 2, surface: 'a', lemma: 'a', reading: 'a', senses: []},
      {sub: 1, wordId: 3, surface: 'b', lemma: 'b', reading: 'b', senses: []}],
  };
}

class FakeDictionary {
  pages: {chapterId: number, page: number, reply: Subject<EpubPageTokens | null>}[] = [];
  lookups: {request: DictionaryLookupRequest, reply: Subject<DictionaryLookupResult | null>}[] = [];
  prioritized: number[] = [];

  getEpubPage(chapterId: number, page: number) {
    const reply = new Subject<EpubPageTokens | null>();
    this.pages.push({chapterId, page, reply});
    return reply;
  }

  lookup(request: DictionaryLookupRequest) {
    const reply = new Subject<DictionaryLookupResult | null>();
    this.lookups.push({request, reply});
    return reply;
  }

  prioritizeChapter(chapterId: number) {
    this.prioritized.push(chapterId);
    return of({});
  }

  answerPage(i: number, tokens: EpubPageTokens | null) {
    this.pages[i].reply.next(tokens);
    this.pages[i].reply.complete();
  }

  answerLookup(i: number, result: DictionaryLookupResult | null) {
    this.lookups[i].reply.next(result);
    this.lookups[i].reply.complete();
  }
}

class FakePresenter {
  visible = false;
  handlers: PopupHandlers | null = null;
  views: DictionaryPopupView[] = [];
  shown: {vertical: boolean, viaKeyboard: boolean}[] = [];

  show(_anchor: Range, options: {vertical: boolean, viaKeyboard: boolean}, handlers: PopupHandlers) {
    this.visible = true;
    this.handlers = handlers;
    this.shown.push(options);
  }

  render(view: DictionaryPopupView) {
    this.views.push(view);
  }

  hide() {
    this.visible = false;
  }

  isVisible() {
    return this.visible;
  }

  get view() {
    return this.views[this.views.length - 1];
  }

  /** What the real presenter does on Close, Escape, an outside tap or the word leaving the screen */
  closeFromPopup(reason: PopupCloseReason = 'close-button') {
    this.visible = false;
    this.handlers?.closed(reason);
  }
}

function hitOn(node: Text, s: number, e: number, endNode: Node = node, endOffset = e): EpubHit {
  const range = document.createRange();
  range.setStart(node, s);
  range.setEnd(endNode, endOffset);
  return {blockIndex: 4, token: [s, e, 1, null, null], s, e, offset: s, range};
}

describe('ReaderDictionaryController', () => {
  let dictionary: FakeDictionary;
  let presenter: FakePresenter;
  let messages: Subject<Message<DictionaryPagesEvent>>;
  let hit: EpubHit | null;
  /** The tokens the controller last handed the locator */
  let seenPage: EpubPageTokens | null;
  let highlighter: {mark: ReturnType<typeof vi.fn>, clear: ReturnType<typeof vi.fn>};
  let controller: ReaderDictionaryController;
  let container: HTMLElement;

  beforeEach(() => {
    dictionary = new FakeDictionary();
    presenter = new FakePresenter();
    messages = new Subject();
    highlighter = {mark: vi.fn(), clear: vi.fn()};
    seenPage = null;
    container = document.createElement('div');
    // Block 4 reads 今日の朝刊と、新聞社, and the hit is 新聞社 at 7 to 10
    container.innerHTML = '<p data-kb="4">今日の朝刊と、新聞社</p>';
    document.body.appendChild(container);
    hit = hitOn(container.querySelector('p')!.firstChild as Text, 7, 10);

    TestBed.configureTestingModule({
      providers: [
        ReaderDictionaryController,
        {provide: DictionaryService, useValue: dictionary},
        {provide: MessageHubService, useValue: {messages$: messages}},
        {provide: DictionaryPopupPresenter, useValue: presenter},
        {provide: DICTIONARY_WORD_LOCATOR, useValue: (_x: number, _y: number, _c: Element, page: EpubPageTokens) => {
          seenPage = page;
          return hit;
        }},
        {provide: DICTIONARY_HIGHLIGHTER, useValue: highlighter},
      ],
    });
    controller = TestBed.inject(ReaderDictionaryController);
    controller.attach(container, {vertical: () => true});
  });

  afterEach(() => {
    controller.detach();
    container.remove();
    document.getSelection()?.removeAllRanges();
  });

  const click = () => new MouseEvent('click', {clientX: 10, clientY: 10, detail: 1});

  function ready(event: EVENTS, chapterId: number, pages: number[]) {
    messages.next({event, payload: {seriesId: 1, chapterId, pages}});
  }

  function openOnPage() {
    controller.setPage(5, 2);
    dictionary.answerPage(0, TOKENS);
    expect(controller.handleClick(click())).toBe(true);
  }

  it('loads the page tokens and turns a click on a word into a lookup', () => {
    controller.setPage(5, 2);
    expect(dictionary.pages[0]).toMatchObject({chapterId: 5, page: 2});
    expect(controller.isLookupClick(click())).toBe(false);

    dictionary.answerPage(0, TOKENS);
    expect(controller.isLookupClick(click())).toBe(true);
    expect(presenter.visible).toBe(false);

    expect(controller.handleClick(click())).toBe(true);
    expect(dictionary.lookups[0].request).toEqual({chapterId: 5, page: 2, block: 4, s: 7, sub: null});
    expect(presenter.shown[0]).toEqual({vertical: true, viaKeyboard: false});
    expect(presenter.view.state).toEqual({kind: 'loading'});
    expect(presenter.view.surface).toBe('新聞社');
    expect(highlighter.mark).toHaveBeenCalledWith(hit?.range);
    expect(controller.isPopupVisible()).toBe(true);

    dictionary.answerLookup(0, lookupResult('新聞社'));
    expect(presenter.view.state).toMatchObject({kind: 'result'});
  });

  it('waits for DictionaryPagesReady when the page is not tokenized yet', () => {
    controller.setPage(5, 2);
    dictionary.answerPage(0, null);
    expect(controller.handleClick(click())).toBe(false);

    ready(EVENTS.DictionaryPagesReady, 5, [7]);
    ready(EVENTS.DictionaryPagesReady, 6, [2]);
    expect(dictionary.pages).toHaveLength(1);

    ready(EVENTS.DictionaryPagesReady, 5, [1, 2]);
    expect(dictionary.pages).toHaveLength(2);
    dictionary.answerPage(1, TOKENS);
    expect(controller.isLookupClick(click())).toBe(true);
  });

  it('drops a page response that arrives after the page changed', () => {
    controller.setPage(5, 2);
    controller.setPage(5, 3);
    // The request for page 2 is unsubscribed, which is the guard
    expect(dictionary.pages[0].reply.observed).toBe(false);
    dictionary.answerPage(1, null);
    dictionary.answerPage(0, TOKENS);
    expect(controller.isLookupClick(click())).toBe(false);

    ready(EVENTS.DictionaryPagesReady, 5, [3]);
    dictionary.answerPage(2, TOKENS);
    expect(controller.isLookupClick(click())).toBe(true);
  });

  it('loads again when DictionaryPagesReady arrives while the request is out and the answer is a 204', () => {
    controller.setPage(5, 2);
    ready(EVENTS.DictionaryPagesReady, 5, [2]);
    expect(dictionary.pages).toHaveLength(1);

    dictionary.answerPage(0, null);
    expect(dictionary.pages).toHaveLength(2);
    expect(dictionary.pages[1]).toMatchObject({chapterId: 5, page: 2});
    const tokens = {...TOKENS, version: 'v2'};
    dictionary.answerPage(1, tokens);
    expect(controller.isLookupClick(click())).toBe(true);
    expect(seenPage).toBe(tokens);
  });

  it('reloads a loaded page when it is tokenized again', () => {
    controller.setPage(5, 2);
    dictionary.answerPage(0, TOKENS);
    ready(EVENTS.DictionaryPagesReady, 5, [2]);
    expect(dictionary.pages).toHaveLength(2);
    const newer = {...TOKENS, version: 'v2'};
    dictionary.answerPage(1, newer);
    expect(controller.isLookupClick(click())).toBe(true);
    expect(seenPage).toBe(newer);
  });

  it('takes the surface from the block text, leaving out ruby readings', () => {
    container.innerHTML = '<p data-kb="4"><ruby>漢字<rt>かんじ</rt></ruby>です</p>';
    const base = container.querySelector('ruby')!.firstChild as Text;
    const after = container.querySelector('p')!.lastChild as Text;
    hit = hitOn(base, 0, 2, after, 0);
    expect(hit.range.toString()).toBe('漢字かんじ');

    openOnPage();
    expect(presenter.view.surface).toBe('漢字');
  });

  it('returns false off a word and while text is selected', () => {
    controller.setPage(5, 2);
    dictionary.answerPage(0, TOKENS);
    hit = null;
    expect(controller.handleClick(click())).toBe(false);
    expect(presenter.shown).toHaveLength(0);

    hit = hitOn(container.querySelector('p')!.firstChild as Text, 7, 10);
    document.getSelection()?.addRange(hit.range.cloneRange());
    expect(controller.isLookupClick(click())).toBe(false);
    expect(controller.handleClick(click())).toBe(false);
  });

  it('pushes a part onto the stack and goes back to the whole word', () => {
    openOnPage();
    dictionary.answerLookup(0, lookupResult('新聞社'));

    presenter.handlers?.part(1);
    expect(dictionary.lookups[1].request.sub).toBe(1);
    expect(presenter.view).toMatchObject({state: {kind: 'loading'}, canGoBack: true, openSub: 1});
    dictionary.answerLookup(1, lookupResult('社'));
    expect(presenter.view.state).toMatchObject({kind: 'result', result: {word: {lemma: '社'}}});

    presenter.handlers?.back();
    expect(presenter.view).toMatchObject({canGoBack: false, openSub: null});
    expect(presenter.view.state).toMatchObject({kind: 'result', result: {word: {lemma: '新聞社'}}});
    expect(dictionary.lookups).toHaveLength(2);
  });

  it('ignores a part answer that arrives after Back', () => {
    openOnPage();
    dictionary.answerLookup(0, lookupResult('新聞社'));
    presenter.handlers?.part(0);
    presenter.handlers?.back();
    dictionary.answerLookup(1, lookupResult('新聞'));
    expect(presenter.view.state).toMatchObject({kind: 'result', result: {word: {lemma: '新聞社'}}});
  });

  it('shows the failure when a lookup errors', () => {
    openOnPage();
    dictionary.lookups[0].reply.error(new Error('500'));
    expect(presenter.view.state).toEqual({kind: 'failed'});
  });

  it('looks the word up again when its analysis arrives', () => {
    openOnPage();
    dictionary.answerLookup(0, lookupResult('辛い'));

    ready(EVENTS.DictionaryAnalysisReady, 5, [3]);
    expect(dictionary.lookups).toHaveLength(1);

    ready(EVENTS.DictionaryAnalysisReady, 5, [2]);
    expect(dictionary.lookups).toHaveLength(2);
    expect(dictionary.lookups[1].request.sub).toBeNull();
    const renders = presenter.views.length;
    dictionary.answerLookup(1, lookupResult('辛い', {senseKey: '1:1', reading: null, generator: 'claude'}));
    expect(presenter.views.length).toBe(renders + 1);
    expect(presenter.view.state).toMatchObject({kind: 'result', result: {analysis: {senseKey: '1:1'}}});

    ready(EVENTS.DictionaryAnalysisReady, 5, [2]);
    expect(dictionary.lookups).toHaveLength(2);
  });

  it('looks the word up once more when its analysis arrives during the first lookup', () => {
    openOnPage();
    ready(EVENTS.DictionaryAnalysisReady, 5, [2]);
    expect(dictionary.lookups).toHaveLength(1);

    dictionary.answerLookup(0, lookupResult('辛い'));
    expect(presenter.view.state).toMatchObject({kind: 'result', result: {analysis: null}});
    expect(dictionary.lookups).toHaveLength(2);
    expect(dictionary.lookups[1].request.sub).toBeNull();

    dictionary.answerLookup(1, lookupResult('辛い', {senseKey: '1:1', reading: null, generator: 'claude'}));
    expect(presenter.view.state).toMatchObject({kind: 'result', result: {analysis: {senseKey: '1:1'}}});
    expect(dictionary.lookups).toHaveLength(2);
  });

  it('does not look up again when the first answer already has the analysis', () => {
    openOnPage();
    ready(EVENTS.DictionaryAnalysisReady, 5, [2]);
    dictionary.answerLookup(0, lookupResult('辛い', {senseKey: '1:1', reading: null, generator: 'claude'}));
    expect(dictionary.lookups).toHaveLength(1);
  });

  it('consumes the click that follows an outside dismissal, for every handler that asks', () => {
    openOnPage();
    presenter.closeFromPopup('outside');
    hit = null;
    const blank = click();
    expect(controller.consumesClick(blank)).toBe(true);
    expect(controller.consumesClick(blank)).toBe(true);

    const later = click();
    expect(controller.consumesClick(later)).toBe(false);
  });

  it('does not consume a click on another word, so it opens there', () => {
    openOnPage();
    presenter.closeFromPopup('outside');
    const onWord = click();
    expect(controller.consumesClick(onWord)).toBe(false);
    expect(controller.handleClick(onWord)).toBe(true);
    expect(controller.isPopupVisible()).toBe(true);
  });

  it('consumes nothing after the window or after a close that was not an outside tap', () => {
    vi.useFakeTimers();
    try {
      openOnPage();
      presenter.closeFromPopup('outside');
      hit = null;
      vi.advanceTimersByTime(DISMISS_CLICK_WINDOW_MS + 1);
      expect(controller.consumesClick(click())).toBe(false);

      hit = hitOn(container.querySelector('p')!.firstChild as Text, 7, 10);
      controller.handleClick(click());
      presenter.closeFromPopup('close-button');
      hit = null;
      expect(controller.consumesClick(click())).toBe(false);
    } finally {
      vi.useRealTimers();
    }
  });

  it('clears the highlight when the popup closes itself or is hidden', () => {
    openOnPage();
    presenter.closeFromPopup();
    expect(highlighter.clear).toHaveBeenCalledTimes(1);
    expect(controller.isPopupVisible()).toBe(false);

    controller.handleClick(click());
    controller.hidePopup();
    expect(highlighter.clear).toHaveBeenCalledTimes(2);
    expect(presenter.visible).toBe(false);
  });

  it('closes the popup when the page changes', () => {
    openOnPage();
    controller.setPage(5, 3);
    expect(controller.isPopupVisible()).toBe(false);
    expect(highlighter.clear).toHaveBeenCalled();
  });

  it('prioritizes each chapter once', () => {
    controller.openChapter(5);
    controller.openChapter(5);
    controller.openChapter(6);
    expect(dictionary.prioritized).toEqual([5, 6]);
  });

  it('stops listening for events after detach', () => {
    controller.setPage(5, 2);
    dictionary.answerPage(0, null);
    controller.detach();
    ready(EVENTS.DictionaryPagesReady, 5, [2]);
    expect(dictionary.pages).toHaveLength(1);
    expect(document.getElementById('kavita-dict-open-style')).toBeNull();
  });
});
