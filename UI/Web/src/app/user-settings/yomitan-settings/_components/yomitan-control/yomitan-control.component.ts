import {ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked} from '@angular/core';
import {TranslocoDirective} from "@jsverse/transloco";
import {debounce, disabled, form, FormField, max, min, required} from "@angular/forms/signals";
import {YomitanSettingsService} from "../../_services/yomitan-settings.service";
import {YomitanControlDef, YomitanSelectOption} from "../../_models/yomitan-options";
import {sameValue} from "../../_helpers/yomitan-settings-util";
import {FormFieldDirective} from "../../../../_directives/form-field.directive";
import {ValidationErrorsComponent} from "../../../../shared/_components/validation-errors/validation-errors.component";
import {generateUniqueId} from "../../../../_helpers/random";

/** Text and number inputs wait for typing to pause before saving */
const INPUT_DEBOUNCE_MS = 400;

/**
 * A single input bound to one Yomitan option. Reads the value from YomitanSettingsService and saves
 * every valid change straight back through it.
 */
@Component({
  selector: 'app-yomitan-control',
  imports: [
    TranslocoDirective,
    FormField,
    FormFieldDirective,
    ValidationErrorsComponent
  ],
  templateUrl: './yomitan-control.component.html',
  styleUrl: './yomitan-control.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class YomitanControlComponent {

  private readonly settingsService = inject(YomitanSettingsService);

  control = input.required<YomitanControlDef>();
  /** Shown beside a checkbox */
  label = input<string>('');
  /** Used as the input's id, generated when not given */
  inputId = input<string | undefined>(undefined);
  disabled = input(false);
  /** Smaller inputs for use inside lists */
  compact = input(false);

  private readonly generatedId = generateUniqueId();
  protected readonly id = computed(() => this.inputId() ?? this.generatedId);

  protected readonly value = computed(() => {
    const control = this.control();
    return this.settingsService.get(control.path, control.global);
  });

  protected readonly selectOptions = computed<YomitanSelectOption[]>(() => {
    const options = this.control().options;
    if (options === 'mainDictionary') {
      const dictionaries = this.settingsService.dictionaries()
        .filter(d => d.sequenced)
        .map(d => ({value: d.title, label: d.title}));
      return [{value: '', key: 'main-dictionary-none'}, ...dictionaries];
    }
    return options ?? [];
  });

  protected readonly booleanModel = signal({value: false});
  protected readonly booleanForm = form(this.booleanModel, (path) => {
    disabled(path.value, {when: () => this.disabled()});
  });

  protected readonly numberModel = signal<{value: number | null}>({value: 0});
  protected readonly numberForm = form(this.numberModel, (path) => {
    disabled(path.value, {when: () => this.disabled()});
    required(path.value);
    min(path.value, () => this.control().min);
    max(path.value, () => this.control().max);
    debounce(path.value, INPUT_DEBOUNCE_MS);
  });

  protected readonly textModel = signal({value: ''});
  protected readonly textForm = form(this.textModel, (path) => {
    disabled(path.value, {when: () => this.disabled()});
    debounce(path.value, INPUT_DEBOUNCE_MS);
  });

  protected readonly selectModel = signal({value: ''});
  protected readonly selectForm = form(this.selectModel, (path) => {
    disabled(path.value, {when: () => this.disabled()});
  });

  constructor() {
    // Pull the stored value into the matching form whenever it changes
    effect(() => {
      const value = this.value();
      const type = this.control().type;
      untracked(() => this.pull(type, value));
    });

    effect(() => {
      const value = this.booleanModel().value;
      untracked(() => {
        if (!this.isType('toggle', 'checkbox')) return;
        this.push(value, value);
      });
    });

    effect(() => {
      const value = this.numberModel().value;
      untracked(() => {
        if (!this.isType('number') || value === null || !this.numberForm().valid()) return;
        this.push(value, value);
      });
    });

    effect(() => {
      const value = this.textModel().value;
      untracked(() => {
        if (!this.isType('text', 'password', 'textarea')) return;
        this.push(value, this.control().transform === 'tags' ? splitTags(value) : value);
      });
    });

    effect(() => {
      const value = this.selectModel().value;
      untracked(() => {
        if (!this.isType('select')) return;
        this.push(value, this.control().numeric ? Number(value) : value);
      });
    });
  }

  private isType(...types: string[]) {
    return types.includes(this.control().type);
  }

  /**
   * The stored option as the form holds it: a boolean, a number, or text for everything else.
   */
  private toModelValue(type: string, value: unknown): boolean | number | string {
    switch (type) {
      case 'toggle':
      case 'checkbox':
        return !!value;
      case 'number':
        return typeof value === 'number' ? value : Number(value ?? 0);
      case 'select':
        return value === null || value === undefined ? '' : `${value}`;
      default:
        if (this.control().transform === 'tags') return Array.isArray(value) ? value.join(' ') : '';
        return typeof value === 'string' ? value : '';
    }
  }

  private pull(type: string, value: unknown) {
    const modelValue = this.toModelValue(type, value);
    switch (type) {
      case 'toggle':
      case 'checkbox':
        if (this.booleanModel().value !== modelValue) this.booleanModel.set({value: modelValue as boolean});
        break;
      case 'number':
        if (this.numberModel().value !== modelValue) this.numberModel.set({value: modelValue as number});
        break;
      case 'select':
        if (this.selectModel().value !== modelValue) this.selectModel.set({value: modelValue as string});
        break;
      default: {
        const current = this.textModel().value;
        // Leave the text alone when it only differs in tag spacing
        if (this.control().transform === 'tags' && sameValue(splitTags(current), value)) break;
        if (current !== modelValue) this.textModel.set({value: modelValue as string});
        break;
      }
    }
  }

  /**
   * Saves a change the user made. A form value that matches the stored one, such as the value the
   * form was just filled with, is not saved back.
   */
  private push(modelValue: unknown, value: unknown) {
    const control = this.control();
    const stored = this.value();
    if (modelValue === this.toModelValue(control.type, stored) || sameValue(value, stored)) return;
    void this.settingsService.set(control.path, value, control.global);
  }
}

/**
 * Yomitan's splitTags data transform, used for Anki tag lists.
 */
function splitTags(value: string) {
  return value.split(/[,; ]+/).filter(tag => tag.length > 0);
}
