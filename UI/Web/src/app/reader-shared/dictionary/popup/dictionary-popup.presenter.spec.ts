import {TestBed} from '@angular/core/testing';
import {TranslocoTestingModule} from '@jsverse/transloco';
import en from '../../../../assets/langs/en.json';
import {DictionaryPopupPresenter, PopupHandlers} from './dictionary-popup.presenter';
import {DictionaryPopupView} from './popup-state';

const VIEW: DictionaryPopupView = {state: {kind: 'loading'}, surface: '辛い', canGoBack: false, openSub: null};

describe('DictionaryPopupPresenter', () => {
  let presenter: DictionaryPopupPresenter;
  let handlers: PopupHandlers & {closedCalls: number};
  let word: HTMLElement;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [TranslocoTestingModule.forRoot({
        langs: {en},
        translocoConfig: {availableLangs: ['en'], defaultLang: 'en'},
        preloadLangs: true,
      })],
      providers: [DictionaryPopupPresenter],
    });
    presenter = TestBed.inject(DictionaryPopupPresenter);
    handlers = {part: vi.fn(), back: vi.fn(), closed() { this.closedCalls++; }, closedCalls: 0};
    word = document.createElement('p');
    word.textContent = '辛い';
    word.tabIndex = 0;
    document.body.appendChild(word);
  });

  afterEach(() => {
    presenter.hide();
    word.remove();
  });

  function open() {
    const range = document.createRange();
    range.selectNodeContents(word);
    // jsdom has no layout, so the range gets a box by hand
    range.getBoundingClientRect = () => ({left: 10, top: 10, width: 40, height: 24}) as DOMRect;
    word.focus();
    presenter.show(range, {vertical: false, viaKeyboard: false}, handlers);
    presenter.render(VIEW);
    TestBed.tick();
  }

  const pane = () => document.querySelector<HTMLElement>('.cdk-overlay-pane app-dictionary-popup');

  it('renders the popup in an overlay without a backdrop', () => {
    open();
    expect(presenter.isVisible()).toBe(true);
    expect(pane()?.textContent).toContain(en['dictionary-popup'].loading);
    expect(document.querySelector('.cdk-overlay-backdrop')).toBeNull();
  });

  it('closes on Escape, stops the key and returns focus', () => {
    open();
    const event = new KeyboardEvent('keydown', {key: 'Escape', bubbles: true, cancelable: true});
    const reader = vi.fn();
    document.addEventListener('keydown', reader);
    pane()?.querySelector<HTMLElement>('[data-testid="popup"]')?.focus();
    document.body.dispatchEvent(event);
    document.removeEventListener('keydown', reader);

    expect(presenter.isVisible()).toBe(false);
    expect(handlers.closedCalls).toBe(1);
    expect(reader).not.toHaveBeenCalled();
    expect(document.activeElement).toBe(word);
  });

  it('closes on a pointer-down outside and not on one inside', () => {
    open();
    pane()?.dispatchEvent(new Event('pointerdown', {bubbles: true}));
    expect(presenter.isVisible()).toBe(true);

    document.body.dispatchEvent(new Event('pointerdown', {bubbles: true}));
    expect(presenter.isVisible()).toBe(false);
    expect(handlers.closedCalls).toBe(1);
  });

  it('closes on Close and passes part chips and Back on', () => {
    open();
    presenter.render({...VIEW, canGoBack: true, state: {kind: 'result', result: {
      word: {id: 1, surface: 'a', lemma: 'a', pos: 'n', isProper: false, occurrenceReading: null, reading: 'a', readings: [], jlpt: null},
      senses: [], analysis: null,
      parts: [{sub: 3, wordId: 2, surface: 'b', lemma: 'b', reading: 'b', senses: []}],
    }}});
    TestBed.tick();
    pane()?.querySelector<HTMLElement>('[data-testid="popup-part"]')?.click();
    pane()?.querySelector<HTMLElement>('[data-testid="popup-back"]')?.click();
    expect(handlers.part).toHaveBeenCalledWith(3);
    expect(handlers.back).toHaveBeenCalledTimes(1);

    pane()?.querySelector<HTMLElement>('[data-testid="popup-close"]')?.click();
    expect(presenter.isVisible()).toBe(false);
    expect(handlers.closedCalls).toBe(1);
    expect(pane()).toBeNull();
  });

  it('hides without telling the handlers when the caller closes it', () => {
    open();
    presenter.hide();
    expect(presenter.isVisible()).toBe(false);
    expect(handlers.closedCalls).toBe(0);
  });
});
