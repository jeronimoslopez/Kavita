import {ChangeDetectionStrategy, Component, computed, inject} from '@angular/core';
import {TranslocoDirective} from "@jsverse/transloco";
import {SettingItemComponent} from "../../../../settings/_components/setting-item/setting-item.component";
import {SettingSwitchComponent} from "../../../../settings/_components/setting-switch/setting-switch.component";
import {YomitanSettingComponent} from "../yomitan-setting/yomitan-setting.component";
import {YomitanControlComponent} from "../yomitan-control/yomitan-control.component";
import {YomitanHelpComponent} from "../yomitan-help/yomitan-help.component";
import {YomitanInputChange, YomitanInputFieldComponent} from "../yomitan-input-field/yomitan-input-field.component";
import {YomitanSettingsService} from "../../_services/yomitan-settings.service";
import {
  YOMITAN_SCANNING_PREVENTION_SETTINGS,
  YOMITAN_SCANNING_SETTINGS
} from "../../_models/yomitan-setting-definitions";
import {YomitanControlDef, YomitanModification, YomitanScanInput} from "../../_models/yomitan-options";
import {
  getModifierDisplayValue,
  isMouseInput,
  joinModifiers,
  MODIFIER_KEYS,
  splitModifiers
} from "../../_helpers/yomitan-input-util";

interface CheckboxOption {
  property: string;
  key: string;
}

const TYPE_OPTIONS: CheckboxOption[] = [
  {property: 'types.mouse', key: 'scan-input-type-mouse'},
  {property: 'types.touch', key: 'scan-input-type-touch'},
  {property: 'types.pen', key: 'scan-input-type-pen'},
];
const SEARCH_OPTIONS: CheckboxOption[] = [
  {property: 'options.searchTerms', key: 'scan-input-search-terms'},
  {property: 'options.searchKanji', key: 'scan-input-search-kanji'},
];
const TOUCH_OPTIONS: CheckboxOption[] = [
  {property: 'options.scanOnTouchTap', key: 'scan-input-scan-on-touch-tap'},
  {property: 'options.scanOnTouchPress', key: 'scan-input-scan-on-touch-press'},
  {property: 'options.scanOnTouchRelease', key: 'scan-input-scan-on-touch-release'},
  {property: 'options.scanOnTouchMove', key: 'scan-input-scan-on-touch-move'},
  {property: 'options.preventTouchScrolling', key: 'scan-input-prevent-touch-scrolling'},
];
const PEN_OPTIONS: CheckboxOption[] = [
  {property: 'options.scanOnPenPress', key: 'scan-input-scan-on-pen-press'},
  {property: 'options.scanOnPenRelease', key: 'scan-input-scan-on-pen-release'},
  {property: 'options.scanOnPenMove', key: 'scan-input-scan-on-pen-move'},
  {property: 'options.scanOnPenHover', key: 'scan-input-scan-on-pen-hover'},
  {property: 'options.scanOnPenReleaseHover', key: 'scan-input-scan-on-pen-release-hover'},
  {property: 'options.preventPenScrolling', key: 'scan-input-prevent-pen-scrolling'},
];

const ALL_CHECKBOX_OPTIONS = [...TYPE_OPTIONS, ...SEARCH_OPTIONS, ...TOUCH_OPTIONS, ...PEN_OPTIONS];

/**
 * Same as Yomitan's ScanInputsController.createDefaultMouseInput.
 */
export function createDefaultMouseInput(include: string, exclude: string): YomitanScanInput {
  return {
    include,
    exclude,
    types: {mouse: true, touch: false, pen: false},
    options: {
      showAdvanced: false,
      searchTerms: true,
      searchKanji: true,
      scanOnTouchTap: true,
      scanOnTouchMove: false,
      scanOnTouchPress: false,
      scanOnTouchRelease: false,
      scanOnPenMove: true,
      scanOnPenHover: false,
      scanOnPenReleaseHover: false,
      scanOnPenPress: true,
      scanOnPenRelease: false,
      preventTouchScrolling: true,
      preventPenScrolling: true,
      minimumTouchTime: 0,
    },
  };
}

/**
 * Yomitan's Scanning section, with the Scanning Inputs and Input Action Prevention dialogs shown inline.
 * Scan resolution is left out because Yomitan hides it for Japanese.
 */
@Component({
  selector: 'app-yomitan-scanning-section',
  imports: [
    TranslocoDirective,
    SettingItemComponent,
    SettingSwitchComponent,
    YomitanSettingComponent,
    YomitanControlComponent,
    YomitanHelpComponent,
    YomitanInputFieldComponent
  ],
  templateUrl: './yomitan-scanning-section.component.html',
  styleUrl: './yomitan-scanning-section.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class YomitanScanningSectionComponent {

  protected readonly settingsService = inject(YomitanSettingsService);

  protected readonly settings = YOMITAN_SCANNING_SETTINGS;
  protected readonly preventionSettings = YOMITAN_SCANNING_PREVENTION_SETTINGS;
  protected readonly typeOptions = TYPE_OPTIONS;
  protected readonly searchOptions = SEARCH_OPTIONS;
  protected readonly touchOptions = TOUCH_OPTIONS;
  protected readonly penOptions = PEN_OPTIONS;

  protected readonly inputs = computed(() => this.settingsService.profileOptions()?.scanning.inputs ?? []);
  protected readonly os = this.settingsService.os;

  protected readonly mainInputIndex = computed(() => getIndexOfMainScanInput(this.inputs()));
  protected readonly middleMouseIndex = computed(() => getIndexOfMiddleMouseScanInput(this.inputs()));

  protected readonly mainModifierKey = computed(() => {
    const index = this.mainInputIndex();
    if (index < 0) return 'other';
    return splitModifiers(this.inputs()[index].include)[0] ?? 'none';
  });
  protected readonly mainModifierOptions = computed(() => {
    const os = this.os();
    const options: {value: string, label?: string, key?: string}[] = [{value: 'none', key: 'scan-modifier-none'}];
    for (const key of MODIFIER_KEYS) {
      options.push({value: key, label: getModifierDisplayValue(key, os)});
    }
    if (this.mainInputIndex() < 0) options.push({value: 'other', key: 'scan-modifier-other'});
    return options;
  });
  protected readonly middleMouseEnabled = computed(() => {
    const index = this.middleMouseIndex();
    return index >= 0 && splitModifiers(this.inputs()[index].include).includes('mouse2');
  });

  protected readonly inputEntries = computed(() => this.inputs().map((input, index) => ({
    index,
    input,
    include: splitModifiers(input.include),
    exclude: splitModifiers(input.exclude),
    pointerTypes: (['mouse', 'touch', 'pen'] as const).filter(type => input.types[type]),
    minimumTouchTimeControl: {path: ['scanning', 'inputs', index, 'options', 'minimumTouchTime'], type: 'number'} as YomitanControlDef,
    checkboxes: Object.fromEntries(ALL_CHECKBOX_OPTIONS.map(option => [
      option.property,
      {path: ['scanning', 'inputs', index, ...option.property.split('.')], type: 'checkbox'} as YomitanControlDef,
    ])),
  })));

  /**
   * Same as Yomitan's ScanInputsSimpleController: the main input requires the chosen key and
   * excludes the primary mouse button.
   */
  setMainModifierKey(event: Event) {
    const value = (event.target as HTMLSelectElement).value;
    if (value === 'other') return;
    const include = joinModifiers(value === 'none' ? [] : [value]);
    const index = this.mainInputIndex();
    if (index < 0) {
      void this.modifyInputs([{action: 'push', path: 'scanning.inputs', items: [createDefaultMouseInput(include, 'mouse0')]}]);
    } else {
      void this.settingsService.set(['scanning', 'inputs', index, 'include'], include);
    }
  }

  setMiddleMouseEnabled(event: Event) {
    const enabled = (event.target as HTMLInputElement).checked;
    const index = this.middleMouseIndex();
    if (enabled) {
      if (index >= 0) return;
      const mainIndex = this.mainInputIndex();
      const start = mainIndex >= 0 ? mainIndex + 1 : this.inputs().length;
      void this.modifyInputs([{action: 'splice', path: 'scanning.inputs', start, deleteCount: 0, items: [createDefaultMouseInput('mouse2', '')]}]);
    } else if (index >= 0) {
      void this.modifyInputs([{action: 'splice', path: 'scanning.inputs', start: index, deleteCount: 1, items: []}]);
    }
  }

  addInput() {
    void this.modifyInputs([{action: 'push', path: 'scanning.inputs', items: [createDefaultMouseInput('', '')]}]);
  }

  removeInput(index: number) {
    void this.modifyInputs([{action: 'splice', path: 'scanning.inputs', start: index, deleteCount: 1, items: []}]);
  }

  setInputModifiers(index: number, property: 'include' | 'exclude', change: YomitanInputChange) {
    void this.settingsService.set(['scanning', 'inputs', index, property], joinModifiers(change.modifiers));
  }

  clearInputs(index: number) {
    void this.modifyInputs([
      {action: 'set', path: ['scanning', 'inputs', index, 'include'], value: ''},
      {action: 'set', path: ['scanning', 'inputs', index, 'exclude'], value: ''},
    ]);
  }

  setShowAdvanced(index: number, value: boolean) {
    void this.settingsService.set(['scanning', 'inputs', index, 'options', 'showAdvanced'], value);
  }

  private modifyInputs(targets: YomitanModification[]) {
    return this.settingsService.modifyProfile(targets);
  }
}

function getIndexOfMainScanInput(inputs: YomitanScanInput[]) {
  return inputs.findIndex(({include, exclude, types: {mouse}}) => {
    if (!mouse) return false;
    const includeValues = splitModifiers(include);
    const excludeValues = splitModifiers(exclude);
    return (includeValues.length === 0 || (includeValues.length === 1 && !isMouseInput(includeValues[0])))
      && excludeValues.length === 1 && excludeValues[0] === 'mouse0';
  });
}

function getIndexOfMiddleMouseScanInput(inputs: YomitanScanInput[]) {
  return inputs.findIndex(({include, exclude, types: {mouse}}) => {
    if (!mouse) return false;
    const includeValues = splitModifiers(include);
    return includeValues.length === 1 && includeValues[0] === 'mouse2' && splitModifiers(exclude).length === 0;
  });
}
