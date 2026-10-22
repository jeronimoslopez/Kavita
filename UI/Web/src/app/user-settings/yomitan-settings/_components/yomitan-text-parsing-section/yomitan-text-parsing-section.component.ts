import {ChangeDetectionStrategy, Component, computed, inject} from '@angular/core';
import {TranslocoDirective} from "@jsverse/transloco";
import {YomitanSettingComponent} from "../yomitan-setting/yomitan-setting.component";
import {YomitanHelpComponent} from "../yomitan-help/yomitan-help.component";
import {YomitanSettingsService} from "../../_services/yomitan-settings.service";
import {YOMITAN_TEXT_PARSING_SETTINGS} from "../../_models/yomitan-setting-definitions";
import {YomitanTerminationCharacter} from "../../_models/yomitan-options";

/**
 * The sentence parsing part of Yomitan's Text Parsing section, with the Sentence Termination Characters
 * dialog shown inline. The internal parser, MeCab and word spacing only affect Yomitan's search page.
 */
@Component({
  selector: 'app-yomitan-text-parsing-section',
  imports: [
    TranslocoDirective,
    YomitanSettingComponent,
    YomitanHelpComponent
  ],
  templateUrl: './yomitan-text-parsing-section.component.html',
  styleUrl: './yomitan-text-parsing-section.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class YomitanTextParsingSectionComponent {

  private readonly settingsService = inject(YomitanSettingsService);

  protected readonly settings = YOMITAN_TEXT_PARSING_SETTINGS;
  protected readonly showCharacters = computed(() => {
    const mode = this.settingsService.profileOptions()?.sentenceParsing.terminationCharacterMode;
    return this.settingsService.showAdvanced() && (mode === 'custom' || mode === 'custom-no-newlines');
  });
  protected readonly characters = computed(() => this.settingsService.profileOptions()?.sentenceParsing.terminationCharacters ?? []);

  add() {
    const entry: YomitanTerminationCharacter = {
      enabled: true,
      character1: '"',
      character2: '"',
      includeCharacterAtStart: false,
      includeCharacterAtEnd: false,
    };
    void this.settingsService.modifyProfile([{action: 'push', path: 'sentenceParsing.terminationCharacters', items: [entry]}]);
  }

  remove(index: number) {
    void this.settingsService.modifyProfile([{action: 'splice', path: 'sentenceParsing.terminationCharacters', start: index, deleteCount: 1, items: []}]);
  }

  async reset() {
    const defaults = await this.settingsService.getDefaultOptions();
    void this.settingsService.set('sentenceParsing.terminationCharacters', defaults.profiles[0].options.sentenceParsing.terminationCharacters);
  }

  setProperty(index: number, property: keyof YomitanTerminationCharacter, value: unknown) {
    void this.settingsService.set(['sentenceParsing', 'terminationCharacters', index, property], value);
  }

  setChecked(index: number, property: keyof YomitanTerminationCharacter, event: Event) {
    this.setProperty(index, property, (event.target as HTMLInputElement).checked);
  }

  /**
   * A quote has a closing character, a terminator doesn't. Switching to quote starts with a matching pair.
   */
  setType(index: number, event: Event) {
    const isQuote = (event.target as HTMLSelectElement).value === 'quote';
    this.setProperty(index, 'character2', isQuote ? this.characters()[index].character1 : null);
  }

  setCharacter(index: number, property: 'character1' | 'character2', event: Event) {
    const input = event.target as HTMLInputElement;
    const value = input.value.substring(0, 1);
    if (property === 'character2' && this.characters()[index].character2 === null) return;
    this.setProperty(index, property, value);
  }
}
