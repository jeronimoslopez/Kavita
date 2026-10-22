import {ChangeDetectionStrategy, Component} from '@angular/core';
import {TranslocoDirective} from "@jsverse/transloco";
import {SettingItemComponent} from "../../../../settings/_components/setting-item/setting-item.component";
import {YomitanSettingComponent} from "../yomitan-setting/yomitan-setting.component";
import {YOMITAN_GENERAL_SETTINGS} from "../../_models/yomitan-setting-definitions";

/**
 * Yomitan's General section. Kavita only ships Japanese dictionaries, so the language is fixed.
 * The welcome guide, context menu and Yomitan API options only apply to the browser extension.
 */
@Component({
  selector: 'app-yomitan-general-section',
  imports: [
    TranslocoDirective,
    SettingItemComponent,
    YomitanSettingComponent
  ],
  templateUrl: './yomitan-general-section.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class YomitanGeneralSectionComponent {
  protected readonly enableSetting = YOMITAN_GENERAL_SETTINGS[0];
  protected readonly otherSettings = YOMITAN_GENERAL_SETTINGS.slice(1);
}
