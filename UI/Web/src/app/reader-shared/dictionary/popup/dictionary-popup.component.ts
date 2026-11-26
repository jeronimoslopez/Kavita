import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  ElementRef,
  Injector,
  inject,
  input,
  output,
  untracked,
  viewChild,
} from '@angular/core';
import {TranslocoDirective} from '@jsverse/transloco';
import {posLabels} from '../pos';
import {orderSenses} from '../senses';
import {toHiragana} from '../furigana';
import {groupKey} from '../readings';
import {DictionaryPopupMode, DictionaryPopupView} from './popup-state';

/**
 * The dictionary entry for one tapped word, after shimen's Popup. It only renders. Where it sits on screen and when
 * it closes belong to DictionaryPopupPresenter
 */
@Component({
  selector: 'app-dictionary-popup',
  imports: [TranslocoDirective],
  templateUrl: './dictionary-popup.component.html',
  styleUrl: './dictionary-popup.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DictionaryPopupComponent {

  private readonly injector = inject(Injector);

  readonly view = input.required<DictionaryPopupView>();
  readonly mode = input<DictionaryPopupMode>('popover');
  /** Focus moves into the popup only when it was opened from the keyboard */
  readonly viaKeyboard = input(false);

  /** The `sub` index of the part chip that was pressed */
  readonly part = output<number>();
  /** The `index` of the Part of chip that was pressed */
  readonly phrase = output<number>();
  readonly back = output<void>();
  readonly dismiss = output<void>();
  /** The groupKey of the reading tab that was pressed */
  readonly group = output<string>();
  /** The reading to save for the word, always the selected group's first, so a choice names its group the same way */
  readonly chooseReading = output<string>();
  readonly clearReading = output<void>();

  private readonly panel = viewChild.required<ElementRef<HTMLElement>>('panel');

  protected readonly state = computed(() => this.view().state);
  protected readonly result = computed(() => {
    const state = this.state();
    return state.kind === 'result' ? state.result : null;
  });
  protected readonly word = computed(() => this.result()?.word ?? null);
  protected readonly parts = computed(() => this.result()?.parts ?? []);
  protected readonly partOf = computed(() => this.result()?.partOf ?? []);
  protected readonly readings = computed(() => this.result()?.readings ?? null);
  /** Empty for a result without readings and for bookOnly, which both list every sense */
  protected readonly groups = computed(() => {
    const readings = this.readings();
    return readings === null || readings.bookOnly ? [] : readings.groups;
  });
  protected readonly tabs = computed(() => {
    const groups = this.groups();
    return groups.length < 2 ? [] : groups.map(g => ({group: g, key: groupKey(g), others: g.readings.slice(1).join('、')}));
  });
  protected readonly selected = computed(() => {
    const groups = this.groups();
    const key = this.view().openGroup;
    return groups.find(g => groupKey(g) === key) ?? groups[0] ?? null;
  });
  protected readonly selectedKey = computed(() => {
    const selected = this.selected();
    return selected === null ? null : groupKey(selected);
  });
  protected readonly firstSelected = computed(() => this.selected() === (this.groups()[0] ?? null));
  /**
   * The headword's reading, which follows the selected tab. On the first tab it's the book's or the hint's printed
   * reading, else the reading that picked the group, else the group's first
   */
  protected readonly headwordReading = computed(() => {
    const word = this.word();
    const readings = this.readings();
    const selected = this.selected();
    if (word === null) return null;
    if (readings === null) return word.reading;
    if (selected === null) return readings.shown ?? word.reading;
    if (this.firstSelected()) return readings.shown ?? readings.pick ?? selected.readings[0] ?? word.reading;
    return selected.readings[0] ?? word.reading;
  });
  /** The en.json key of the label that says where the first group's reading came from, shown while it's selected */
  protected readonly sourceLabel = computed(() => {
    const readings = this.readings();
    if (readings === null || !this.firstSelected()) return null;
    if (readings.level === 'guess') return 'best-guess';
    switch (readings.source) {
      case 'furigana':
      case 'book':
        return 'from-book';
      case 'series':
        return 'from-series';
      case 'choice':
        return 'your-choice';
      default:
        return null;
    }
  });
  /** Use on a tab that isn't the saved choice, stop on the one that is, and nothing without tabs */
  protected readonly choiceAction = computed(() => {
    if (!this.view().canChoose || this.tabs().length === 0) return null;
    return this.firstSelected() && this.readings()?.source === 'choice' ? 'clear' : 'use';
  });
  protected readonly senses = computed(() => {
    const result = this.result();
    const senses = this.selected()?.senses ?? result?.senses ?? [];
    return result === null ? [] : orderSenses(senses, result.analysis).map(s => ({
      ...s,
      gloss: s.sense.gloss.join('; '),
      pos: posLabels(s.sense.pos).join(', '),
    }));
  });
  /** The reading the analysis chose, only when it isn't the dictionary's usual one */
  protected readonly aiReading = computed(() => {
    const result = this.result();
    const reading = result?.analysis?.reading;
    if (result === null || !reading) return null;
    return toHiragana(reading) === toHiragana(result.word.reading) ? null : reading;
  });
  protected readonly hasAi = computed(() => this.aiReading() !== null || this.senses().some(s => s.chosen));
  protected readonly label = computed(() => this.word()?.lemma ?? (this.view().surface || null));

  constructor() {
    effect(() => {
      if (!this.viaKeyboard()) return;
      untracked(() => afterNextRender(() => this.panel().nativeElement.focus({preventScroll: true}),
        {injector: this.injector}));
    });
  }

  protected choose() {
    const reading = this.selected()?.readings[0];
    if (reading !== undefined) this.chooseReading.emit(reading);
  }
}
