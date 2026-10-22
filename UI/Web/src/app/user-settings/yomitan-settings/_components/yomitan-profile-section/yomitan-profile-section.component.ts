import {ChangeDetectionStrategy, Component, computed, inject} from '@angular/core';
import {translate, TranslocoDirective} from "@jsverse/transloco";
import {SettingItemComponent} from "../../../../settings/_components/setting-item/setting-item.component";
import {YomitanControlComponent} from "../yomitan-control/yomitan-control.component";
import {YomitanSettingsService} from "../../_services/yomitan-settings.service";
import {YomitanControlDef, YomitanModification} from "../../_models/yomitan-options";
import {createCopyName, generateYomitanId} from "../../_helpers/yomitan-settings-util";
import {ConfirmService} from "../../../../shared/confirm.service";

/**
 * Yomitan's Profile section with the Profiles dialog shown inline. Profile conditions are left out,
 * they pick profiles by website URL, which doesn't apply inside Kavita.
 */
@Component({
  selector: 'app-yomitan-profile-section',
  imports: [
    TranslocoDirective,
    SettingItemComponent,
    YomitanControlComponent
  ],
  templateUrl: './yomitan-profile-section.component.html',
  styleUrl: './yomitan-profile-section.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class YomitanProfileSectionComponent {

  private readonly settingsService = inject(YomitanSettingsService);
  private readonly confirmService = inject(ConfirmService);

  protected readonly profileCurrent = this.settingsService.profileIndex;
  protected readonly profiles = computed(() => (this.settingsService.options()?.profiles ?? []).map((profile, index) => ({
    index,
    name: profile.name,
    nameControl: {path: ['profiles', index, 'name'], type: 'text', global: true} as YomitanControlDef,
  })));
  protected readonly currentName = computed(() => this.profiles()[this.profileCurrent()]?.name ?? '');

  setActiveProfile(event: Event) {
    this.setDefaultProfile(Number((event.target as HTMLSelectElement).value));
  }

  setDefaultProfile(index: number) {
    if (index === this.profileCurrent()) return;
    void this.settingsService.set('profileCurrent', index, true);
  }

  /**
   * Yomitan's Add button duplicates the profile being edited.
   */
  add() {
    this.duplicate(this.profileCurrent());
  }

  duplicate(index: number) {
    const options = this.settingsService.options();
    if (options === null) return;
    const profile = structuredClone(options.profiles[index]);
    profile.name = createCopyName(profile.name, options.profiles.map(p => p.name));
    profile.id = generateYomitanId(16);
    void this.settingsService.modify([{action: 'push', path: 'profiles', items: [profile]}]);
  }

  async copyFrom(destinationIndex: number, event: Event) {
    const select = event.target as HTMLSelectElement;
    const sourceIndex = Number(select.value);
    select.value = '';
    const options = this.settingsService.options();
    if (options === null || Number.isNaN(sourceIndex) || sourceIndex === destinationIndex) return;

    const source = options.profiles[sourceIndex];
    const destination = options.profiles[destinationIndex];
    if (!await this.confirmService.confirm(translate('yomitan-settings.profile-copy-confirm', {source: source.name, destination: destination.name}))) return;
    void this.settingsService.set(['profiles', destinationIndex, 'options'], structuredClone(source.options), true);
  }

  async reset(index: number) {
    const options = this.settingsService.options();
    if (options === null) return;
    const name = options.profiles[index].name;
    if (!await this.confirmService.confirm(translate('yomitan-settings.profile-reset-confirm', {name}))) return;

    const defaults = await this.settingsService.getDefaultOptions();
    const profile = defaults.profiles[0];
    profile.name = name;
    await this.settingsService.set(['profiles', index], profile, true);
    await this.settingsService.reloadDictionaries();
  }

  async delete(index: number) {
    const options = this.settingsService.options();
    if (options === null || options.profiles.length <= 1) return;
    if (!await this.confirmService.confirm(translate('yomitan-settings.profile-delete-confirm', {name: options.profiles[index].name}))) return;

    // Like Yomitan's ProfileController.deleteProfile, profileCurrent shifts down with the profiles after it
    const targets: YomitanModification[] = [{action: 'splice', path: 'profiles', start: index, deleteCount: 1, items: []}];
    const current = options.profileCurrent;
    if (current >= index) {
      targets.push({action: 'set', path: 'profileCurrent', value: Math.max(0, current - 1)});
    }
    void this.settingsService.modify(targets);
  }

  /**
   * Swaps neighbouring profiles and keeps profileCurrent on the same profile.
   */
  move(index: number, offset: number) {
    const options = this.settingsService.options();
    const target = index + offset;
    if (options === null || target < 0 || target >= options.profiles.length) return;

    const targets: YomitanModification[] = [{action: 'swap', path1: ['profiles', index], path2: ['profiles', target]}];
    const current = options.profileCurrent;
    const newCurrent = current === index ? target : (current === target ? index : current);
    if (newCurrent !== current) {
      targets.push({action: 'set', path: 'profileCurrent', value: newCurrent});
    }
    void this.settingsService.modify(targets);
  }
}
