import {ChangeDetectionStrategy, Component} from '@angular/core';
import {YomitanSettingComponent} from "../yomitan-setting/yomitan-setting.component";
import {YOMITAN_POPUP_SIZE_SETTINGS} from "../../_models/yomitan-setting-definitions";

/**
 * Yomitan's Popup Position & Size section.
 */
@Component({
  selector: 'app-yomitan-popup-size-section',
  imports: [
    YomitanSettingComponent
  ],
  templateUrl: './yomitan-popup-size-section.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class YomitanPopupSizeSectionComponent {
  protected readonly settings = YOMITAN_POPUP_SIZE_SETTINGS;
}
