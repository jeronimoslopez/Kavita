import {ChangeDetectionStrategy, Component, computed, inject} from '@angular/core';
import {TranslocoDirective} from "@jsverse/transloco";
import {YomitanSettingComponent} from "../yomitan-setting/yomitan-setting.component";
import {YomitanControlComponent} from "../yomitan-control/yomitan-control.component";
import {YomitanHelpComponent} from "../yomitan-help/yomitan-help.component";
import {YomitanSettingsService} from "../../_services/yomitan-settings.service";
import {
  YOMITAN_APPEARANCE_CSS_SETTINGS,
  YOMITAN_APPEARANCE_SETTINGS
} from "../../_models/yomitan-setting-definitions";
import {YomitanControlDef, YomitanSelectOption} from "../../_models/yomitan-options";

export const COLLAPSIBLE_OPTIONS: YomitanSelectOption[] = [
  {value: 'not-collapsible', key: 'collapsible-not-collapsible'},
  {value: 'collapsed', key: 'collapsible-collapsed'},
  {value: 'expanded', key: 'collapsible-expanded'},
  {value: 'force-collapsed', key: 'collapsible-force-collapsed'},
  {value: 'force-expanded', key: 'collapsible-force-expanded'},
];

/**
 * Yomitan's Appearance section, with the Collapsible Dictionaries and Custom CSS dialogs shown inline.
 * Debug information and the parsed text reading mode are left out, they only show on Yomitan's search page.
 */
@Component({
  selector: 'app-yomitan-appearance-section',
  imports: [
    TranslocoDirective,
    YomitanSettingComponent,
    YomitanControlComponent,
    YomitanHelpComponent
  ],
  templateUrl: './yomitan-appearance-section.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class YomitanAppearanceSectionComponent {

  protected readonly settingsService = inject(YomitanSettingsService);

  protected readonly settings = YOMITAN_APPEARANCE_SETTINGS;
  protected readonly cssSettings = YOMITAN_APPEARANCE_CSS_SETTINGS;

  protected readonly collapsibleControls = computed(() => this.settingsService.dictionaryEntries().map(entry => ({
    entry,
    control: {path: ['dictionaries', entry.index, 'definitionsCollapsible'], type: 'select', options: COLLAPSIBLE_OPTIONS} as YomitanControlDef,
  })));
}
