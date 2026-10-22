import {ChangeDetectionStrategy, Component, computed, inject} from '@angular/core';
import {TranslocoDirective} from "@jsverse/transloco";
import {SettingSwitchComponent} from "../../../../settings/_components/setting-switch/setting-switch.component";
import {SettingItemComponent} from "../../../../settings/_components/setting-item/setting-item.component";
import {YomitanSettingComponent} from "../yomitan-setting/yomitan-setting.component";
import {YomitanControlComponent} from "../yomitan-control/yomitan-control.component";
import {YomitanSettingsService} from "../../_services/yomitan-settings.service";
import {YOMITAN_POPUP_SETTINGS} from "../../_models/yomitan-setting-definitions";
import {YomitanControlDef} from "../../_models/yomitan-options";

/**
 * Yomitan's Popup Behavior section. Scanning the search page is left out, Kavita has no search page.
 */
@Component({
  selector: 'app-yomitan-popup-section',
  imports: [
    TranslocoDirective,
    SettingSwitchComponent,
    SettingItemComponent,
    YomitanSettingComponent,
    YomitanControlComponent
  ],
  templateUrl: './yomitan-popup-section.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class YomitanPopupSectionComponent {

  private readonly settingsService = inject(YomitanSettingsService);

  protected readonly settings = YOMITAN_POPUP_SETTINGS;
  protected readonly nestingDepthControl: YomitanControlDef = {path: 'scanning.popupNestingMaxDepth', type: 'number', min: 1, step: 1};
  protected readonly nestingDepth = computed(() => this.settingsService.profileOptions()?.scanning.popupNestingMaxDepth ?? 0);

  /**
   * Same as Yomitan's NestedPopupsController: on means one level of child popups unless more are set.
   */
  setNestedPopupsEnabled(event: Event) {
    const enabled = (event.target as HTMLInputElement).checked;
    if (enabled && this.nestingDepth() > 0) return;
    void this.settingsService.set('scanning.popupNestingMaxDepth', enabled ? 1 : 0);
  }
}
