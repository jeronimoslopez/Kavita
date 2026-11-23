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
import {DictionaryPopupPresenter, PopupHandlers} from './popup/dictionary-popup.presenter';
import {DictionaryPopupView} from './popup/popup-state';
import {
  DICTIONARY_HIGHLIGHTER,
  DICTIONARY_WORD_LOCATOR,
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

  /** What the real presenter does on Close, Escape or an outside tap */
  closeFromPopup() {
    this.visible = false;
    this.handlers?.closed();
  }
}

describe('ReaderDictionaryController', () => {
  let dictionary: FakeDictionary;
  let presenter: FakePresenter;
  let messages: Subject<Message<DictionaryPagesEvent>>;
  let hit: EpubHit | null;
  let highlighter: {mark: ReturnType<typeof vi.fn>, clear: ReturnType<typeof vi.fn>};
  let controller: ReaderDictionaryController;
  let container: HTMLElement;

  beforeEach(() => {
    dictionary = new FakeDictionary();
    presenter = new FakePresenter();
    messages = new Subject();
    highlighter = {mark: vi.fn(), clear: vi.fn()};
    container = document.createElement('div');
    container.textContent = '新聞社';
    document.body.appendChild(container);
    const range = document.createRange();
    range.selectNodeContents(container);
    hit = {blockIndex: 4, token: [7, 10, 1, null, null], s: 7, e: 10, offset: 8, range};

    TestBed.configureTestingModule({
      providers: [
        ReaderDictionaryController,
        {provide: DictionaryService, useValue: dictionary},
        {provide: MessageHubService, useValue: {messages$: messages}},
        {provide: DictionaryPopupPresenter, useValue: presenter},
        {provide: DICTIONARY_WORD_LOCATOR, useValue: () => hit},
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
    dictionary.answerPage(0, TOKENS);
    expect(controller.isLookupClick(click())).toBe(false);
    dictionary.answerPage(1, TOKENS);
    expect(controller.isLookupClick(click())).toBe(true);
  });

  it('returns false off a word and while text is selected', () => {
    controller.setPage(5, 2);
    dictionary.answerPage(0, TOKENS);
    hit = null;
    expect(controller.handleClick(click())).toBe(false);
    expect(presenter.shown).toHaveLength(0);

    const range = document.createRange();
    range.selectNodeContents(container);
    hit = {blockIndex: 4, token: [7, 10, 1, null, null], s: 7, e: 10, offset: 8, range};
    document.getSelection()?.addRange(range);
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
