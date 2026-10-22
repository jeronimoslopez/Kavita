import {ChangeDetectionStrategy, Component, inject, signal} from '@angular/core';
import {translate, TranslocoDirective} from "@jsverse/transloco";
import {ToastrService} from "@openng/ngx-toastr";
import {YomitanSettingsService} from "../../_services/yomitan-settings.service";
import {YomitanOptions, YomitanProfileOptions} from "../../_models/yomitan-options";
import {downloadJson} from "../../_helpers/yomitan-settings-util";
import {ConfirmService} from "../../../../shared/confirm.service";
import {ConfirmConfig} from "../../../../shared/confirm-dialog/_models/confirm-config";

/** Highest backup format version Yomitan's BackupController reads and writes */
const BACKUP_VERSION = 0;

interface OptionsUtil {
  prepare(): Promise<void>;
  update(options: unknown): Promise<YomitanOptions>;
}

interface PendingImport {
  options: YomitanOptions;
  warnings: string[];
}

/**
 * Yomitan's Backup section. Exported files use Yomitan's backup format, so they import into the
 * browser extension and back. The dictionary collection export is left out, Kavita's dictionaries
 * live on the server.
 */
@Component({
  selector: 'app-yomitan-backup-section',
  imports: [
    TranslocoDirective
  ],
  templateUrl: './yomitan-backup-section.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class YomitanBackupSectionComponent {

  private readonly settingsService = inject(YomitanSettingsService);
  private readonly confirmService = inject(ConfirmService);
  private readonly toastr = inject(ToastrService);

  protected readonly busy = signal(false);
  protected readonly importError = signal<string | null>(null);
  /** An import waiting on the user's answer to Yomitan's security warning */
  protected readonly pendingImport = signal<PendingImport | null>(null);

  async exportSettings() {
    const date = new Date();
    const options = structuredClone(this.settingsService.options());
    if (options === null) return;

    const [environment, defaultTemplates] = await Promise.all([
      this.settingsService.invoke('getEnvironmentInfo').catch(() => null),
      this.settingsService.invoke<string>('getDefaultAnkiFieldTemplates').catch(() => null),
    ]);
    for (const {options: profileOptions} of options.profiles) {
      if (profileOptions.anki.fieldTemplates === defaultTemplates || !profileOptions.anki.fieldTemplates) {
        profileOptions.anki.fieldTemplates = null;
      }
    }

    const data = {
      version: BACKUP_VERSION,
      date: getExportDateString(date, '-', ' ', ':', 6),
      url: window.location.origin,
      environment,
      userAgent: navigator.userAgent,
      options,
    };
    downloadJson(data, `yomitan-settings-${getExportDateString(date, '-', '-', '-', 6)}.json`);
  }

  async onImportFile(event: Event) {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;

    this.importError.set(null);
    this.pendingImport.set(null);
    this.busy.set(true);
    try {
      const options = await this.readSettingsFile(file);
      const warnings = getSanitizationWarnings(options, true);
      if (warnings.length > 0) {
        this.pendingImport.set({options, warnings});
        return;
      }
      await this.applyImport(options);
    } catch (e) {
      console.error('[Yomitan] settings import failed', e);
      this.importError.set(e instanceof Error ? e.message : `${e}`);
    } finally {
      this.busy.set(false);
    }
  }

  async confirmPendingImport(sanitize: boolean) {
    const pending = this.pendingImport();
    if (pending === null) return;
    this.pendingImport.set(null);
    if (sanitize) getSanitizationWarnings(pending.options, false);
    this.busy.set(true);
    try {
      await this.applyImport(pending.options);
    } finally {
      this.busy.set(false);
    }
  }

  cancelPendingImport() {
    this.pendingImport.set(null);
  }

  async resetSettings() {
    const config = new ConfirmConfig();
    config.header = translate('yomitan-settings.backup-reset-header');
    config.content = translate('yomitan-settings.backup-reset-confirm');
    config.buttons = [
      {text: translate('yomitan-settings.cancel'), type: 'secondary'},
      {text: translate('yomitan-settings.backup-reset-confirm-button'), type: 'danger'},
    ];
    if (!await this.confirmService.confirm(undefined, config)) return;

    this.busy.set(true);
    try {
      const defaults = await this.settingsService.getDefaultOptions();
      if (await this.settingsService.setAll(defaults)) {
        this.toastr.success(translate('yomitan-settings.backup-reset-success'));
      }
    } finally {
      this.busy.set(false);
    }
  }

  private async applyImport(options: YomitanOptions) {
    if (await this.settingsService.setAll(options)) {
      this.toastr.success(translate('yomitan-settings.backup-import-success'));
    }
  }

  /**
   * The checks of Yomitan's BackupController._importSettingsFile, then OptionsUtil.update to bring
   * older settings up to the current version.
   */
  private async readSettingsFile(file: File): Promise<YomitanOptions> {
    const data = JSON.parse(await file.text());
    if (data === null || typeof data !== 'object' || Array.isArray(data)) {
      throw new Error(`Invalid data type: ${typeof data}`);
    }

    const version = data.version;
    if (!(typeof version === 'number' && Number.isFinite(version) && version === Math.floor(version))) {
      throw new Error(`Invalid version: ${version}`);
    }
    if (!(version >= 0 && version <= BACKUP_VERSION)) {
      throw new Error(`Unsupported version: ${version}`);
    }

    const options = data.options;
    if (options === null || typeof options !== 'object' || Array.isArray(options)) {
      throw new Error(`Invalid options type: ${typeof options}`);
    }

    const {OptionsUtil} = await this.settingsService.importYomitanModule<{OptionsUtil: new () => OptionsUtil}>('js/data/options-util.js');
    const optionsUtil = new OptionsUtil();
    await optionsUtil.prepare();
    return await optionsUtil.update(options);
  }
}

/**
 * Yomitan's BackupController._getSettingsExportDateString.
 */
function getExportDateString(date: Date, dateSeparator: string, dateTimeSeparator: string, timeSeparator: string, resolution: number) {
  const values = [
    date.getUTCFullYear().toString(),
    dateSeparator,
    (date.getUTCMonth() + 1).toString().padStart(2, '0'),
    dateSeparator,
    date.getUTCDate().toString().padStart(2, '0'),
    dateTimeSeparator,
    date.getUTCHours().toString().padStart(2, '0'),
    timeSeparator,
    date.getUTCMinutes().toString().padStart(2, '0'),
    timeSeparator,
    date.getUTCSeconds().toString().padStart(2, '0'),
  ];
  return values.slice(0, resolution * 2 - 1).join('');
}

function isLocalhostUrl(urlString: string) {
  try {
    const url = new URL(urlString);
    switch (url.hostname.toLowerCase()) {
      case 'localhost':
      case '127.0.0.1':
      case '[::1]':
        return url.protocol === 'http:' || url.protocol === 'https:';
    }
  } catch (e) {
    // Not a URL
  }
  return false;
}

/**
 * Yomitan's _settingsImportSanitizeOptions. With dryRun the options are only checked, otherwise the
 * risky values are replaced.
 */
function getSanitizationWarnings(options: YomitanOptions, dryRun: boolean) {
  const warnings = new Set<string>();
  for (const profile of options.profiles ?? []) {
    const profileOptions: YomitanProfileOptions | undefined = profile?.options;
    if (!profileOptions) continue;

    const anki = profileOptions.anki;
    if (anki && typeof anki === 'object') {
      if (typeof anki.fieldTemplates === 'string') {
        warnings.add('anki.fieldTemplates contains a non-default value');
        if (!dryRun) anki.fieldTemplates = null;
      }
      if (typeof anki.server === 'string' && anki.server.length > 0 && !isLocalhostUrl(anki.server)) {
        warnings.add('anki.server uses a non-localhost URL');
        if (!dryRun) anki.server = 'http://127.0.0.1:8765';
      }
    }

    const sources = profileOptions.audio?.sources;
    if (Array.isArray(sources)) {
      sources.forEach((source, i) => {
        if (source && typeof source.url === 'string' && source.url.length > 0 && !isLocalhostUrl(source.url)) {
          warnings.add(`audio.sources[${i}].url uses a non-localhost URL`);
          if (!dryRun) source.url = '';
        }
      });
    }
  }
  return [...warnings];
}
