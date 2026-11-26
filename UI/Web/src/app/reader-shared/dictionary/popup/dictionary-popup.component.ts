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

  private readonly panel = viewChild.required<ElementRef<HTMLElement>>('panel');

  protected readonly state = computed(() => this.view().state);
  protected readonly result = computed(() => {
    const state = this.state();
    return state.kind === 'result' ? state.result : null;
  });
  protected readonly word = computed(() => this.result()?.word ?? null);
  protected readonly parts = computed(() => this.result()?.parts ?? []);
  protected readonly partOf = computed(() => this.result()?.partOf ?? []);
  protected readonly senses = computed(() => {
    const result = this.result();
    return result === null ? [] : orderSenses(result.senses, result.analysis).map(s => ({
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
}
