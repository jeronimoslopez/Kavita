import {TestBed} from '@angular/core/testing';
import {Location} from '@angular/common';
import {provideLocationMocks, SpyLocation} from '@angular/common/testing';
import {TranslocoTestingModule} from '@jsverse/transloco';
import en from '../../../../assets/langs/en.json';
import {DictionaryPopupPresenter, PopupCloseReason, PopupHandlers} from './dictionary-popup.presenter';
import {DictionaryPopupView} from './popup-state';

const VIEW: DictionaryPopupView = {state: {kind: 'loading'}, surface: '辛い', canGoBack: false, openSub: null};

describe('DictionaryPopupPresenter', () => {
  let presenter: DictionaryPopupPresenter;
  let reasons: PopupCloseReason[];
  let handlers: PopupHandlers;
  let word: HTMLElement;
  /** The tapped word's box, which a spec moves to stand in for scrolling. jsdom has no layout */
  let rect: {left: number, top: number, width: number, height: number};
  const root = document.documentElement;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [TranslocoTestingModule.forRoot({
        langs: {en},
        translocoConfig: {availableLangs: ['en'], defaultLang: 'en'},
        preloadLangs: true,
      })],
      providers: [DictionaryPopupPresenter, provideLocationMocks()],
    });
    presenter = TestBed.inject(DictionaryPopupPresenter);
    reasons = [];
    handlers = {part: vi.fn(), back: vi.fn(), closed: reason => reasons.push(reason)};
    word = document.createElement('p');
    word.textContent = '辛い';
    word.tabIndex = 0;
    document.body.appendChild(word);
    rect = {left: 10, top: 10, width: 40, height: 24};
    Object.defineProperty(root, 'clientWidth', {configurable: true, value: 1280});
    Object.defineProperty(root, 'clientHeight', {configurable: true, value: 800});
  });

  afterEach(() => {
    presenter.hide();
    word.remove();
    delete (root as {clientWidth?: number}).clientWidth;
    delete (root as {clientHeight?: number}).clientHeight;
  });

  function open() {
    const range = document.createRange();
    range.selectNodeContents(word);
    range.getBoundingClientRect = () => ({...rect}) as DOMRect;
    word.focus();
    presenter.show(range, {vertical: false, viaKeyboard: false}, handlers);
    presenter.render(VIEW);
    TestBed.tick();
  }

  const popup = () => document.querySelector<HTMLElement>('.cdk-overlay-pane app-dictionary-popup');
  const overlayPane = () => document.querySelector<HTMLElement>('.cdk-overlay-pane');
  const scroll = () => document.dispatchEvent(new Event('scroll'));

  it('renders the popup in an overlay without a backdrop', () => {
    open();
    expect(presenter.isVisible()).toBe(true);
    expect(popup()?.textContent).toContain(en['dictionary-popup'].loading);
    expect(document.querySelector('.cdk-overlay-backdrop')).toBeNull();
    expect(popup()?.querySelector('.dp-sheet')).toBeNull();
  });

  it('closes on Escape, stops the key and returns focus', () => {
    open();
    const event = new KeyboardEvent('keydown', {key: 'Escape', bubbles: true, cancelable: true});
    const reader = vi.fn();
    document.addEventListener('keydown', reader);
    popup()?.querySelector<HTMLElement>('[data-testid="popup"]')?.focus();
    document.body.dispatchEvent(event);
    document.removeEventListener('keydown', reader);

    expect(presenter.isVisible()).toBe(false);
    expect(reasons).toEqual(['escape']);
    expect(reader).not.toHaveBeenCalled();
    expect(document.activeElement).toBe(word);
  });

  it('closes on a pointer-down outside and not on one inside', () => {
    open();
    popup()?.dispatchEvent(new Event('pointerdown', {bubbles: true}));
    expect(presenter.isVisible()).toBe(true);

    document.body.dispatchEvent(new Event('pointerdown', {bubbles: true}));
    expect(presenter.isVisible()).toBe(false);
    expect(reasons).toEqual(['outside']);
  });

  it('closes on Close and passes part chips and Back on', () => {
    open();
    presenter.render({...VIEW, canGoBack: true, state: {kind: 'result', result: {
      word: {id: 1, surface: 'a', lemma: 'a', pos: 'n', isProper: false, occurrenceReading: null, reading: 'a', readings: [], jlpt: null},
      senses: [], analysis: null,
      parts: [{sub: 3, wordId: 2, surface: 'b', lemma: 'b', reading: 'b', senses: []}],
    }}});
    TestBed.tick();
    popup()?.querySelector<HTMLElement>('[data-testid="popup-part"]')?.click();
    popup()?.querySelector<HTMLElement>('[data-testid="popup-back"]')?.click();
    expect(handlers.part).toHaveBeenCalledWith(3);
    expect(handlers.back).toHaveBeenCalledTimes(1);

    popup()?.querySelector<HTMLElement>('[data-testid="popup-close"]')?.click();
    expect(presenter.isVisible()).toBe(false);
    expect(reasons).toEqual(['close-button']);
    expect(popup()).toBeNull();
  });

  it('hides without telling the handlers when the caller closes it, and returns focus from inside', () => {
    open();
    popup()?.querySelector<HTMLElement>('[data-testid="popup"]')?.focus();
    presenter.hide();
    expect(presenter.isVisible()).toBe(false);
    expect(reasons).toEqual([]);
    expect(document.activeElement).toBe(word);
  });

  it('closes when the word scrolls out of view, and follows it while it is on screen', () => {
    open();
    rect = {...rect, top: 300};
    scroll();
    expect(presenter.isVisible()).toBe(true);
    expect(overlayPane()?.style.marginTop).toBe(`${300 + 24 + 12}px`);

    rect = {...rect, top: -100};
    scroll();
    expect(presenter.isVisible()).toBe(false);
    expect(reasons).toEqual(['out-of-view']);
  });

  it('closes when the page re-rendered and the range collapsed to an empty box', () => {
    open();
    rect = {left: 0, top: 0, width: 0, height: 0};
    scroll();
    expect(reasons).toEqual(['out-of-view']);
  });

  it('caps the popover to the room beside the word when it fits on neither side', () => {
    open();
    const panel = popup()?.querySelector('.dp') as HTMLElement;
    Object.defineProperty(panel, 'scrollHeight', {configurable: true, value: 560});
    rect = {...rect, top: 380};
    scroll();
    // 72vh of 800 is 576, so 560 stays 560 and fits nowhere. Above has 360px, below 376px
    expect(overlayPane()?.style.getPropertyValue('--dp-max-height')).toBe(`${800 - (380 + 24 + 12) - 8}px`);
  });

  it('runs the full close when the location changes', () => {
    open();
    (TestBed.inject(Location) as SpyLocation).simulateUrlPop('/elsewhere');
    expect(presenter.isVisible()).toBe(false);
    expect(reasons).toEqual(['navigation']);
    expect(overlayPane()).toBeNull();
  });

  describe('below 768px', () => {
    const original = window.matchMedia;

    beforeEach(() => {
      window.matchMedia = vi.fn((query: string) => ({matches: false, media: query}) as MediaQueryList);
    });

    afterEach(() => {
      window.matchMedia = original;
    });

    it('is a full-width sheet at the bottom of the screen', () => {
      open();
      expect(window.matchMedia).toHaveBeenCalledWith('(min-width: 768px)');
      expect(popup()?.querySelector('.dp-sheet')).not.toBeNull();
      expect(popup()?.querySelector('.dp-handle')).not.toBeNull();
      expect(overlayPane()?.style.width).toBe('100%');
      expect(document.querySelector('.cdk-overlay-backdrop')).toBeNull();
    });
  });
});
