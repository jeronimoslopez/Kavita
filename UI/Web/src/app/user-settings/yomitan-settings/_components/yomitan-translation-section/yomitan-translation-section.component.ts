import {ChangeDetectionStrategy, Component, computed, inject, signal} from '@angular/core';
import {TranslocoDirective} from "@jsverse/transloco";
import {YomitanSettingComponent} from "../yomitan-setting/yomitan-setting.component";
import {YomitanControlComponent} from "../yomitan-control/yomitan-control.component";
import {YomitanSettingsService} from "../../_services/yomitan-settings.service";
import {
  YOMITAN_TRANSLATION_REPLACEMENTS_SETTINGS,
  YOMITAN_TRANSLATION_SETTINGS
} from "../../_models/yomitan-setting-definitions";
import {YomitanControlDef, YomitanTextReplacement} from "../../_models/yomitan-options";
import {SafeHtmlPipe} from "../../../../_pipes/safe-html.pipe";

/**
 * Yomitan's Translation section, with the Custom Text Replacement Patterns dialog shown inline.
 * Like Yomitan, only the first replacement group is edited.
 */
@Component({
  selector: 'app-yomitan-translation-section',
  imports: [
    TranslocoDirective,
    YomitanSettingComponent,
    YomitanControlComponent,
    SafeHtmlPipe
  ],
  templateUrl: './yomitan-translation-section.component.html',
  styleUrl: './yomitan-translation-section.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class YomitanTranslationSectionComponent {

  private readonly settingsService = inject(YomitanSettingsService);

  protected readonly settings = YOMITAN_TRANSLATION_SETTINGS;
  protected readonly replacementSettings = YOMITAN_TRANSLATION_REPLACEMENTS_SETTINGS;
  /** Entry index to the text typed into its test input */
  protected readonly testInputs = signal<Map<number, string>>(new Map());

  protected readonly replacements = computed(() => {
    const groups = this.settingsService.profileOptions()?.translation.textReplacements.groups ?? [];
    const group = groups[0] ?? [];
    return group.map((replacement, index) => {
      const base = ['translation', 'textReplacements', 'groups', 0, index];
      return {
        index,
        replacement,
        patternControl: {path: [...base, 'pattern'], type: 'text'} as YomitanControlDef,
        replacementControl: {path: [...base, 'replacement'], type: 'text'} as YomitanControlDef,
        ignoreCaseControl: {path: [...base, 'ignoreCase'], type: 'checkbox'} as YomitanControlDef,
      };
    });
  });

  /**
   * Same as Yomitan's addGroup: the first entry also creates the group.
   */
  add() {
    const entry: YomitanTextReplacement = {pattern: '', ignoreCase: false, replacement: ''};
    const groups = this.settingsService.profileOptions()?.translation.textReplacements.groups ?? [];
    if (groups.length === 0) {
      void this.settingsService.modifyProfile([{action: 'splice', path: 'translation.textReplacements.groups', start: 0, deleteCount: 0, items: [[entry]]}]);
    } else {
      void this.settingsService.modifyProfile([{action: 'push', path: ['translation', 'textReplacements', 'groups', 0], items: [entry]}]);
    }
  }

  /**
   * Same as Yomitan's deleteGroup: removing the last entry removes the group.
   */
  remove(index: number) {
    const group = this.settingsService.profileOptions()?.translation.textReplacements.groups[0] ?? [];
    if (group.length > 1) {
      void this.settingsService.modifyProfile([{action: 'splice', path: ['translation', 'textReplacements', 'groups', 0], start: index, deleteCount: 1, items: []}]);
    } else {
      void this.settingsService.modifyProfile([{action: 'splice', path: 'translation.textReplacements.groups', start: 0, deleteCount: group.length, items: []}]);
    }
    this.hideTest(index);
  }

  showTest(index: number) {
    this.testInputs.update(map => new Map(map).set(index, ''));
  }

  hideTest(index: number) {
    this.testInputs.update(map => {
      const next = new Map(map);
      next.delete(index);
      return next;
    });
  }

  setTestInput(index: number, event: Event) {
    const value = (event.target as HTMLInputElement).value;
    this.testInputs.update(map => new Map(map).set(index, value));
  }

  testOutput(replacement: YomitanTextReplacement, input: string) {
    try {
      const regex = new RegExp(replacement.pattern, replacement.ignoreCase ? 'gi' : 'g');
      return input.replace(regex, replacement.replacement);
    } catch (e) {
      return '';
    }
  }
}
