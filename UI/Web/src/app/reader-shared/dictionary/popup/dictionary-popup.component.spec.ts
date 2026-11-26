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
  return {state, surface: '辛い', canGoBack: false, openSub: null, openPhrase: null, ...extra};
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

  it('puts the chosen sense first, tags it and numbers the list in display order', async () => {
    const el = await show(view({kind: 'result', result: result({
      analysis: {senseKey: '100:2', reading: 'からい', generator: 'claude'},
    })}));
    const senses = all(el, 'sense');
    expect(senses.map(s => s.querySelector('.dp-sense-n')?.textContent)).toEqual(['1', '2', '3']);
    expect(senses.map(s => s.getAttribute('data-key'))).toEqual(['100:2', '100:1', '100:3']);
    expect(senses[0].getAttribute('data-chosen')).toBe('true');
    expect(senses[0].querySelector('[data-testid="sense-chosen"]')?.textContent).toBe(strings['used-here']);
    expect(all(el, 'sense-chosen')).toHaveLength(1);
    expect(q(el, 'popup-ai-note')?.textContent).toBe(strings['ai-sense']);
  });

  it('numbers senses from several entries 1 to N with the chosen one first and no key repeated', async () => {
    const keys = ['1365860:1', '1365860:2', '1365860:3', '1365850:1', '1365850:2', '1365850:3', '1365850:4', '2096480:1'];
    const el = await show(view({kind: 'result', result: result({
      senses: keys.map(key => ({key, pos: ['adj-i'], gloss: [`gloss ${key}`]})),
      analysis: {senseKey: '1365850:2', reading: null, generator: 'claude'},
    })}));
    const senses = all(el, 'sense');
    expect(senses.map(s => s.querySelector('.dp-sense-n')?.textContent))
      .toEqual(['1', '2', '3', '4', '5', '6', '7', '8']);
    const rendered = senses.map(s => s.getAttribute('data-key'));
    expect(rendered[0]).toBe('1365850:2');
    expect(senses[0].getAttribute('data-chosen')).toBe('true');
    expect(new Set(rendered).size).toBe(keys.length);
    expect([...rendered].sort()).toEqual([...keys].sort());
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

  it('has no Part of section when partOf is empty or missing', async () => {
    let el = await show(view({kind: 'result', result: result({partOf: []})}));
    expect(q(el, 'popup-part-of')).toBeNull();

    el = await show(view({kind: 'result', result: result()}));
    expect(q(el, 'popup-part-of')).toBeNull();
    expect(all(el, 'popup-phrase')).toHaveLength(0);
  });

  it('renders Part of rows below the parts that emit their index and mark the open phrase', async () => {
    const parts = [{sub: 0, wordId: 2, surface: '気', lemma: '気', reading: 'き', senses: []}];
    const partOf = [
      {index: 0, s: 12, e: 16, lemma: '気がする', reading: 'きがする', gloss: 'to have a feeling that'},
      {index: 1, s: 12, e: 15, lemma: '気が', reading: 'きが', gloss: ''},
    ];
    const el = await show(view({kind: 'result', result: result({parts, partOf})}, {openPhrase: 1}));
    const section = q(el, 'popup-part-of') as HTMLElement;
    expect(section.querySelector('.dp-label')?.textContent).toBe(strings['part-of']);
    const sections = Array.from(el.querySelectorAll('.dp-parts'));
    expect(sections.indexOf(section)).toBe(1);

    expect(section.querySelector('.dp-chip')).toBeNull();
    const rows = all(el, 'popup-phrase');
    expect(rows.map(r => r.querySelector('.dp-row-word')?.textContent?.replace(/\s+/g, ' ').trim()))
      .toEqual(['気がする きがする', '気が きが']);
    expect(rows.map(r => r.getAttribute('aria-pressed'))).toEqual(['false', 'true']);
    expect(rows.every(r => r.localName === 'button' && r.getAttribute('type') === 'button')).toBe(true);
    expect(rows.every(r => r.classList.contains('dp-link-row'))).toBe(true);

    const emitted: number[] = [];
    fixture.componentInstance.phrase.subscribe(index => emitted.push(index));
    rows[0].click();
    expect(emitted).toEqual([0]);
  });

  it('shows the gloss as a second line and leaves it out when it is empty', async () => {
    const partOf = [
      {index: 0, s: 12, e: 16, lemma: '気がする', reading: 'きがする', gloss: 'to have a feeling that'},
      {index: 1, s: 12, e: 15, lemma: '気が', reading: 'きが', gloss: ''},
    ];
    const el = await show(view({kind: 'result', result: result({partOf})}));
    const rows = all(el, 'popup-phrase');
    const gloss = rows[0].querySelector('[data-testid="popup-phrase-gloss"]');
    expect(gloss?.textContent).toBe('to have a feeling that');
    expect(gloss?.classList.contains('dp-meta')).toBe(true);
    expect(gloss?.hasAttribute('lang')).toBe(false);
    expect(rows[1].querySelector('[data-testid="popup-phrase-gloss"]')).toBeNull();
  });

  it('shows a phrase with its senses and no parts, Part of or AI note', async () => {
    const el = await show(view({kind: 'result', result: result({
      word: {
        id: 9, surface: '気がする', lemma: '気がする', pos: '表現', isProper: false,
        occurrenceReading: null, reading: 'きがする', readings: ['きがする'], jlpt: null,
      },
      senses: [{key: '200:1', pos: ['exp', 'vs-i'], gloss: ['to have a feeling that']}],
      parts: [],
      partOf: [],
    })}, {canGoBack: true, openPhrase: 0}));
    expect(q(el, 'popup-lemma')?.textContent).toBe('気がする');
    expect(all(el, 'sense')[0].textContent).toContain('to have a feeling that');
    expect(all(el, 'sense')[0].textContent).toContain('expression, suru verb');
    expect(all(el, 'popup-part')).toHaveLength(0);
    expect(q(el, 'popup-part-of')).toBeNull();
    expect(q(el, 'popup-ai-note')).toBeNull();
    expect(q(el, 'popup-back')).not.toBeNull();
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
