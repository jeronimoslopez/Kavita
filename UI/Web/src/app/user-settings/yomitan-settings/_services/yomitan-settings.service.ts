import {computed, inject, Injectable, signal} from '@angular/core';
import {ToastrService} from "@openng/ngx-toastr";
import {translate} from "@jsverse/transloco";
import {YomitanService} from "../../../_services/yomitan.service";
import {
  YomitanDictionaryInfo,
  YomitanDictionaryOptions,
  YomitanModification,
  YomitanOptions,
  YomitanPath,
  YomitanProfileOptions
} from "../_models/yomitan-options";
import {applyModification, getAtPath, sameValue, serializeModification, toPathArray} from "../_helpers/yomitan-settings-util";

/** Matches the source Yomitan's own settings page passes, so backend listeners can tell the change apart */
const MODIFY_SOURCE = 'kavita-settings';

export interface YomitanDictionaryEntry {
  index: number;
  settings: YomitanDictionaryOptions;
  info: YomitanDictionaryInfo | null;
}

interface ModifyResult {
  result?: unknown;
  error?: {message?: string};
}

/**
 * State for the Yomitan settings section. Holds a local copy of Yomitan's options document and writes
 * every change through Yomitan's backend, which persists it to Kavita. Provided by YomitanSettingsComponent.
 */
@Injectable()
export class YomitanSettingsService {

  private readonly yomitanService = inject(YomitanService);
  private readonly toastr = inject(ToastrService);

  readonly options = signal<YomitanOptions | null>(null);
  readonly dictionaries = signal<YomitanDictionaryInfo[]>([]);
  readonly os = signal<string>('unknown');

  readonly profileIndex = computed(() => this.options()?.profileCurrent ?? 0);
  readonly profileOptions = computed<YomitanProfileOptions | null>(() => {
    const options = this.options();
    if (options === null) return null;
    return options.profiles[options.profileCurrent]?.options ?? options.profiles[0]?.options ?? null;
  });
  /** The profile's dictionary settings in priority order, with each dictionary's summary when it is installed */
  readonly dictionaryEntries = computed<YomitanDictionaryEntry[]>(() => {
    const options = this.profileOptions();
    if (options === null) return [];
    const summaries = new Map(this.dictionaries().map(d => [d.title, d]));
    return options.dictionaries.map((settings, index) => ({index, settings, info: summaries.get(settings.name) ?? null}));
  });
  /** Yomitan's "Advanced" toggle is the profile's general.showAdvanced option */
  readonly showAdvanced = computed(() => this.profileOptions()?.general['showAdvanced'] === true);

  /** Changes reach the backend in the order they were made */
  private queue: Promise<unknown> = Promise.resolve();

  async load() {
    const [options, dictionaries, environment] = await Promise.all([
      this.yomitanService.invoke<YomitanOptions>('optionsGetFull'),
      this.yomitanService.invoke<YomitanDictionaryInfo[]>('getDictionaryInfo'),
      this.yomitanService.invoke<{platform?: {os?: string}}>('getEnvironmentInfo').catch(() => null),
    ]);
    this.options.set(options);
    this.dictionaries.set(dictionaries ?? []);
    this.os.set(environment?.platform?.os ?? 'unknown');
    await this.ensureDictionarySettings();
  }

  async reloadDictionaries() {
    this.dictionaries.set(await this.yomitanService.invoke<YomitanDictionaryInfo[]>('getDictionaryInfo') ?? []);
    await this.ensureDictionarySettings();
  }

  /**
   * Path of a profile option in the options document, for the current profile unless one is given.
   */
  profilePath(path: YomitanPath, profileIndex = this.profileIndex()) {
    return ['profiles', profileIndex, 'options', ...toPathArray(path)];
  }

  get(path: YomitanPath, global = false) {
    const options = this.options();
    if (options === null) return undefined;
    return getAtPath(options, global ? path : this.profilePath(path));
  }

  set(path: YomitanPath, value: unknown, global = false) {
    return this.modify([{action: 'set', path: global ? path : this.profilePath(path), value}]);
  }

  /**
   * Shorthand for modifications relative to the current profile's options.
   */
  modifyProfile(targets: YomitanModification[]) {
    return this.modify(targets.map(target => {
      if (target.action === 'swap') {
        return {...target, path1: this.profilePath(target.path1), path2: this.profilePath(target.path2)};
      }
      return {...target, path: this.profilePath(target.path)};
    }));
  }

  /**
   * Applies the change locally right away so the inputs stay in step, then saves it. A failure is
   * reported and the local copy is reloaded from the backend.
   */
  modify(targets: YomitanModification[]): Promise<boolean> {
    const current = this.options();
    if (current === null || targets.length === 0) return Promise.resolve(false);

    const next = structuredClone(current);
    try {
      for (const target of targets) {
        applyModification(next, target);
      }
    } catch (e) {
      console.error('[Yomitan] invalid settings change', e);
      return Promise.resolve(false);
    }
    this.options.set(next);

    const run = this.queue.then(async () => {
      try {
        const results = await this.yomitanService.invoke<ModifyResult[]>('modifySettings', {
          targets: targets.map(serializeModification),
          source: MODIFY_SOURCE,
        });
        const failed = (results ?? []).find(r => r && typeof r === 'object' && 'error' in r && r.error);
        if (failed) {
          this.onSaveError(failed.error?.message);
          return false;
        }
        return true;
      } catch (e) {
        this.onSaveError(e instanceof Error ? e.message : undefined);
        return false;
      }
    });
    this.queue = run;
    return run;
  }

  /**
   * Replaces the whole options document, as Yomitan's backup import and reset do.
   */
  async setAll(options: YomitanOptions) {
    await this.queue;
    try {
      await this.yomitanService.invoke('setAllSettings', {value: options, source: MODIFY_SOURCE});
    } catch (e) {
      this.onSaveError(e instanceof Error ? e.message : undefined);
      return false;
    }
    this.options.set(await this.yomitanService.invoke<YomitanOptions>('optionsGetFull'));
    await this.ensureDictionarySettings();
    return true;
  }

  async getDefaultOptions(): Promise<YomitanOptions> {
    return await this.yomitanService.getDefaultOptions();
  }

  invoke<T = unknown>(action: string, params?: unknown) {
    return this.yomitanService.invoke<T>(action, params);
  }

  request<T = unknown>(path: string, body?: unknown) {
    return this.yomitanService.request<T>(path, body);
  }

  get assetBaseUrl() {
    return this.yomitanService.assetBaseUrl;
  }

  /**
   * Loads one of Yomitan's vendored modules. Yomitan must be started first, since its modules expect
   * the chrome.* shim to exist.
   */
  async importYomitanModule<T>(relativePath: string): Promise<T> {
    await this.yomitanService.start();
    return await import(/* @vite-ignore */ `${this.assetBaseUrl}/${relativePath}`);
  }

  static createDefaultDictionarySettings(name: string, enabled: boolean, styles?: string): YomitanDictionaryOptions {
    return {
      name,
      alias: name,
      enabled,
      allowSecondarySearches: false,
      definitionsCollapsible: 'not-collapsible',
      partsOfSpeechFilter: true,
      useDeinflections: true,
      styles: styles ?? '',
    };
  }

  /**
   * Adds settings for installed dictionaries a profile doesn't list yet, enabled like a fresh import.
   * Unlike Yomitan this never drops entries, because the server may still be importing them.
   */
  private async ensureDictionarySettings() {
    const options = this.options();
    const installed = this.dictionaries();
    if (options === null || installed.length === 0) return;

    const targets: YomitanModification[] = [];
    options.profiles.forEach((profile, index) => {
      const names = new Set(profile.options.dictionaries.map(d => d.name));
      const missing = installed.filter(d => !names.has(d.title));
      if (missing.length === 0) return;
      targets.push({
        action: 'push',
        path: ['profiles', index, 'options', 'dictionaries'],
        items: missing.map(d => YomitanSettingsService.createDefaultDictionarySettings(d.title, true, d.styles)),
      });
    });
    if (targets.length > 0) {
      await this.modify(targets);
    }
  }

  private onSaveError(message?: string) {
    console.error('[Yomitan] saving settings failed', message);
    this.toastr.error(message ? translate('yomitan-settings.save-error-detail', {message}) : translate('yomitan-settings.save-error'));
    this.yomitanService.invoke<YomitanOptions>('optionsGetFull').then(
      options => {
        if (!sameValue(options, this.options())) this.options.set(options);
      },
      () => {}
    );
  }
}
