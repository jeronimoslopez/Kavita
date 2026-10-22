import {ChangeDetectionStrategy, Component, computed, inject, input} from '@angular/core';
import {translate, TranslocoDirective} from "@jsverse/transloco";
import {SettingItemComponent} from "../../../../settings/_components/setting-item/setting-item.component";
import {SettingSwitchComponent} from "../../../../settings/_components/setting-switch/setting-switch.component";
import {YomitanControlComponent} from "../yomitan-control/yomitan-control.component";
import {YomitanSettingsService} from "../../_services/yomitan-settings.service";
import {YomitanControlDef, YomitanSettingDef} from "../../_models/yomitan-options";
import {YomitanHelpComponent} from "../yomitan-help/yomitan-help.component";

/**
 * Renders one row of Yomitan's settings page from its definition: a switch for a single toggle,
 * otherwise a setting item whose edit view holds every control of the row.
 */
@Component({
  selector: 'app-yomitan-setting',
  imports: [
    TranslocoDirective,
    SettingItemComponent,
    SettingSwitchComponent,
    YomitanControlComponent,
    YomitanHelpComponent
  ],
  templateUrl: './yomitan-setting.component.html',
  styleUrl: './yomitan-setting.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class YomitanSettingComponent {

  private readonly settingsService = inject(YomitanSettingsService);

  setting = input.required<YomitanSettingDef>();

  protected readonly visible = computed(() => {
    const setting = this.setting();
    if (setting.advanced && !this.settingsService.showAdvanced()) return false;
    const options = this.settingsService.profileOptions();
    if (options === null) return false;
    return setting.visibleWhen?.(options) ?? true;
  });

  protected readonly isSwitch = computed(() => {
    const controls = this.setting().controls;
    return controls.length === 1 && controls[0].type === 'toggle';
  });

  /** What the row shows outside of edit mode */
  protected readonly displayValue = computed(() => {
    const setting = this.setting();
    const checkboxes = setting.controls.filter(c => c.type === 'checkbox');
    if (checkboxes.length > 0) {
      const enabled = checkboxes.filter(c => this.settingsService.get(c.path, c.global) === true);
      if (enabled.length === 0) return translate('yomitan-settings.none-selected');
      return enabled.map(c => translate(`yomitan-settings.${c.sub}`)).join(', ');
    }

    const parts = setting.controls.map(control => {
      const text = this.formatValue(control);
      return control.sub ? `${translate(`yomitan-settings.${control.sub}`)}: ${text}` : text;
    });
    return parts.join(', ');
  });

  private formatValue(control: YomitanControlDef) {
    const value = this.settingsService.get(control.path, control.global);
    switch (control.type) {
      case 'select': {
        if (control.options === 'mainDictionary') {
          return value ? `${value}` : translate('yomitan-settings.main-dictionary-none');
        }
        const option = (control.options ?? []).find(o => o.value === `${value}`);
        if (option === undefined) return `${value ?? ''}`;
        return option.label ?? translate(`yomitan-settings.${option.key}`);
      }
      case 'password':
        return value ? '••••••' : translate('yomitan-settings.not-set');
      case 'textarea': {
        const text = typeof value === 'string' ? value.trim() : '';
        if (text.length === 0) return translate('yomitan-settings.not-set');
        const lines = text.split('\n').length;
        return translate('yomitan-settings.line-count', {count: lines});
      }
      default: {
        if (control.transform === 'tags' && Array.isArray(value)) {
          return value.length > 0 ? value.join(' ') : translate('yomitan-settings.not-set');
        }
        if (value === null || value === undefined || value === '') return translate('yomitan-settings.not-set');
        return `${value}`;
      }
    }
  }
}
