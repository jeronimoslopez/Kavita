import {ChangeDetectionStrategy, Component, computed, inject} from '@angular/core';
import {TranslocoDirective} from "@jsverse/transloco";
import {YomitanSettingComponent} from "../yomitan-setting/yomitan-setting.component";
import {YomitanSettingsService} from "../../_services/yomitan-settings.service";
import {YOMITAN_RESULT_DISPLAY_SETTINGS} from "../../_models/yomitan-setting-definitions";

/**
 * Yomitan's Result Display section, with the Secondary Search Dictionaries list shown inline.
 */
@Component({
  selector: 'app-yomitan-result-display-section',
  imports: [
    TranslocoDirective,
    YomitanSettingComponent
  ],
  templateUrl: './yomitan-result-display-section.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class YomitanResultDisplaySectionComponent {

  private readonly settingsService = inject(YomitanSettingsService);
  protected readonly entries = this.settingsService.dictionaryEntries;

  protected readonly modeSettings = YOMITAN_RESULT_DISPLAY_SETTINGS.slice(0, 2);
  protected readonly otherSettings = YOMITAN_RESULT_DISPLAY_SETTINGS.slice(2);
  protected readonly isMerge = computed(() => this.settingsService.profileOptions()?.general['resultOutputMode'] === 'merge');

  setAllowSecondarySearches(index: number, event: Event) {
    void this.settingsService.set(['dictionaries', index, 'allowSecondarySearches'], (event.target as HTMLInputElement).checked);
  }
}
