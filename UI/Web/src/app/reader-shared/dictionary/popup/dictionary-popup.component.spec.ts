import {ComponentFixture, TestBed} from '@angular/core/testing';
import {TranslocoTestingModule} from '@jsverse/transloco';
import en from '../../../../assets/langs/en.json';
import {DictionaryLookupResult} from '../../../_models/dictionary/dictionary';
import {DictionaryPopupComponent} from './dictionary-popup.component';
import {DictionaryPopupState, DictionaryPopupView} from './popup-state';

const strings = en['dictionary-popup'];

function result(overrides: Partial<DictionaryLookupResult> = {}): DictionaryLookupResult {
  return {
    word: {
      id: 1, surface: '辛い', lemma: '辛い', pos: '形容詞', isProper: false,
      occurrenceReading: 'からい', reading: 'からい', readings: ['からい', 'つらい'], jlpt: 3,
    },
    senses: [
      {key: '100:1', pos: ['adj-i'], gloss: ['spicy', 'hot']},
      {key: '100:2', pos: ['adj-i'], gloss: ['painful', 'hard']},
      {key: '100:3', pos: [], gloss: ['salty']},
    ],
    analysis: null,
    parts: [],
    ...overrides,
  };
}

function view(state: DictionaryPopupState, extra: Partial<DictionaryPopupView> = {}): DictionaryPopupView {
  return {state, surface: '辛い', canGoBack: false, openSub: null, ...extra};
}

describe('DictionaryPopupComponent', () => {
  let fixture: ComponentFixture<DictionaryPopupComponent>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [
        DictionaryPopupComponent,
        TranslocoTestingModule.forRoot({
          langs: {en},
          translocoConfig: {availableLangs: ['en'], defaultLang: 'en'},
          preloadLangs: true,
        }),
      ],
    });
    fixture = TestBed.createComponent(DictionaryPopupComponent);
  });

  async function show(v: DictionaryPopupView) {
    fixture.componentRef.setInput('view', v);
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  const q = (el: HTMLElement, id: string) => el.querySelector(`[data-testid="${id}"]`);
  const all = (el: HTMLElement, id: string) => Array.from(el.querySelectorAll<HTMLElement>(`[data-testid="${id}"]`));

  it('shows the tapped text and the loading line while the lookup runs', async () => {
    const el = await show(view({kind: 'loading'}));
    expect(q(el, 'popup-lemma')?.textContent).toBe('辛い');
    expect(q(el, 'popup-loading')?.textContent).toContain(strings.loading);
    expect(q(el, 'popup-close')?.textContent).toContain(strings.close);
    expect(q(el, 'popup-back')).toBeNull();
  });

  it('shows the error message when the lookup failed', async () => {
    const el = await show(view({kind: 'failed'}));
    expect(q(el, 'popup-failed')?.textContent).toContain(strings['lookup-failed']);
    expect(q(el, 'popup-senses')).toBeNull();
  });

  it('says there is no entry when the server had nothing to look up', async () => {
    const el = await show(view({kind: 'empty'}));
    expect(q(el, 'popup-no-entry')?.textContent).toContain(strings['no-entry']);
  });

  it('says there is no entry when the word has no senses', async () => {
    const el = await show(view({kind: 'result', result: result({senses: []})}));
    expect(q(el, 'popup-lemma')?.textContent).toBe('辛い');
    expect(q(el, 'popup-no-entry')?.textContent).toContain(strings['no-entry']);
  });

  it('lists the senses in dictionary order with no tag or AI note when there is no analysis', async () => {
    const el = await show(view({kind: 'result', result: result()}));
    expect(q(el, 'popup-reading')?.textContent).toBe('からい');
    const senses = all(el, 'sense');
    expect(senses.map(s => s.querySelector('.dp-sense-n')?.textContent)).toEqual(['1', '2', '3']);
    expect(senses[0].textContent).toContain('spicy; hot');
    expect(senses[0].textContent).toContain('i-adjective');
    expect(q(el, 'sense-chosen')).toBeNull();
    expect(q(el, 'popup-ai-note')).toBeNull();
    expect(q(el, 'popup-ai-reading')).toBeNull();
  });

  it('puts the chosen sense first, tags it and keeps its number', async () => {
    const el = await show(view({kind: 'result', result: result({
      analysis: {senseKey: '100:2', reading: 'からい', generator: 'claude'},
    })}));
    const senses = all(el, 'sense');
    expect(senses.map(s => s.querySelector('.dp-sense-n')?.textContent)).toEqual(['2', '1', '3']);
    expect(senses[0].getAttribute('data-chosen')).toBe('true');
    expect(senses[0].querySelector('[data-testid="sense-chosen"]')?.textContent).toBe(strings['used-here']);
    expect(all(el, 'sense-chosen')).toHaveLength(1);
    expect(q(el, 'popup-ai-note')?.textContent).toBe(strings['ai-sense']);
  });

  it('shows the AI reading only when it differs from the usual one', async () => {
    let el = await show(view({kind: 'result', result: result({
      analysis: {senseKey: null, reading: 'カライ', generator: 'claude'},
    })}));
    expect(q(el, 'popup-ai-reading')).toBeNull();

    el = await show(view({kind: 'result', result: result({
      analysis: {senseKey: null, reading: 'つらい', generator: 'claude'},
    })}));
    expect(q(el, 'popup-ai-reading')?.textContent).toBe('つらい');
    expect(q(el, 'popup-ai-note')?.textContent).toBe(strings['ai-sense']);
  });

  it('renders part chips that emit their sub index and mark the open part', async () => {
    const parts = [
      {sub: 0, wordId: 2, surface: '新聞', lemma: '新聞', reading: 'しんぶん', senses: []},
      {sub: 1, wordId: 3, surface: '社', lemma: '社', reading: 'しゃ', senses: []},
    ];
    const el = await show(view({kind: 'result', result: result({parts})}, {openSub: 1}));
    const chips = all(el, 'popup-part');
    expect(chips.map(c => c.textContent?.replace(/\s+/g, ' ').trim())).toEqual(['新聞 しんぶん', '社 しゃ']);
    expect(chips.map(c => c.getAttribute('aria-pressed'))).toEqual(['false', 'true']);
    expect(el.querySelector('.dp-label')?.textContent).toBe(strings.parts);

    const emitted: number[] = [];
    fixture.componentInstance.part.subscribe(sub => emitted.push(sub));
    chips[0].click();
    expect(emitted).toEqual([0]);
  });

  it('emits Back and Close', async () => {
    const el = await show(view({kind: 'result', result: result()}, {canGoBack: true}));
    let back = 0;
    let close = 0;
    fixture.componentInstance.back.subscribe(() => back++);
    fixture.componentInstance.dismiss.subscribe(() => close++);
    (q(el, 'popup-back') as HTMLElement).click();
    (q(el, 'popup-close') as HTMLElement).click();
    expect(back).toBe(1);
    expect(close).toBe(1);
  });

  it('is a labelled dialog and draws the sheet handle only as a sheet', async () => {
    let el = await show(view({kind: 'result', result: result()}));
    const dialog = q(el, 'popup') as HTMLElement;
    expect(dialog.getAttribute('role')).toBe('dialog');
    expect(dialog.getAttribute('aria-label')).toBe('辛い');
    expect(el.querySelector('.dp-handle')).toBeNull();

    fixture.componentRef.setInput('mode', 'sheet');
    el = await show(view({kind: 'loading'}, {surface: ''}));
    expect(el.querySelector('.dp-handle')).not.toBeNull();
    expect(q(el, 'popup')?.getAttribute('aria-label')).toBe(strings.senses);
  });

  it('takes focus only when opened from the keyboard', async () => {
    document.body.appendChild(fixture.nativeElement);
    await show(view({kind: 'loading'}));
    expect(document.activeElement).not.toBe(q(fixture.nativeElement, 'popup'));

    const keyboard = TestBed.createComponent(DictionaryPopupComponent);
    document.body.appendChild(keyboard.nativeElement);
    keyboard.componentRef.setInput('view', view({kind: 'loading'}));
    keyboard.componentRef.setInput('viaKeyboard', true);
    await keyboard.whenStable();
    expect(document.activeElement).toBe(q(keyboard.nativeElement, 'popup'));
  });
});
