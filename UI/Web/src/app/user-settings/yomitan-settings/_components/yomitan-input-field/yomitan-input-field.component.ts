import {ChangeDetectionStrategy, Component, computed, input, output} from '@angular/core';
import {TranslocoDirective} from "@jsverse/transloco";
import {
  getActiveButtons,
  getActiveModifiers,
  getInputDisplayValue,
  isModifierKeyCode,
  sortModifiers
} from "../../_helpers/yomitan-input-util";

export interface YomitanInputChange {
  key: string | null;
  modifiers: string[];
}

/**
 * Kavita's take on Yomitan's KeyboardMouseInputField. Focus the field and press keys to record them,
 * or press a mouse button on the mouse icon to record that button. Escape or Backspace clears the
 * field when only modifiers are recorded.
 */
@Component({
  selector: 'app-yomitan-input-field',
  imports: [
    TranslocoDirective
  ],
  templateUrl: './yomitan-input-field.component.html',
  styleUrl: './yomitan-input-field.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class YomitanInputFieldComponent {

  key = input<string | null>(null);
  modifiers = input<string[]>([]);
  os = input<string>('unknown');
  /** Records a non-modifier key as well, for hotkeys */
  keySupported = input(false);
  /** Shows the mouse button that records mouse and pen buttons */
  mouseSupported = input(false);
  /** Pointer types the mouse button accepts */
  pointerTypes = input<string[]>(['mouse', 'pen', 'touch']);
  inputId = input<string | undefined>(undefined);
  placeholder = input<string>('');

  inputChange = output<YomitanInputChange>();

  protected readonly displayValue = computed(() => getInputDisplayValue(this.key(), this.modifiers(), this.os()));

  onKeyDown(event: KeyboardEvent) {
    if (event.key === 'Tab' && !event.altKey && !event.ctrlKey && !event.metaKey) return;
    event.preventDefault();

    let code: string | undefined = event.code;
    if (code === 'Unidentified' || code === '') code = undefined;

    if (this.keySupported()) {
      this.update(getActiveModifiers(event), isModifierKeyCode(code) ? undefined : code);
      return;
    }

    switch (code) {
      case 'Escape':
      case 'Backspace':
        this.update([], null);
        break;
      default:
        this.update([...new Set([...this.modifiers(), ...getActiveModifiers(event)])]);
        break;
    }
  }

  onMouseButtonDown(event: PointerEvent) {
    if (!event.isPrimary || !this.pointerTypes().includes(event.pointerType)) return;
    event.preventDefault();
    this.update([...new Set([...this.modifiers(), ...getActiveButtons(event)])]);
  }

  clear() {
    this.update([], null);
  }

  private update(modifiers: string[], key?: string | null) {
    const sorted = sortModifiers(modifiers);
    const newKey = key === undefined ? this.key() : key;
    if (newKey === this.key() && sorted.join() === this.modifiers().join()) return;
    this.inputChange.emit({key: newKey, modifiers: sorted});
  }
}
