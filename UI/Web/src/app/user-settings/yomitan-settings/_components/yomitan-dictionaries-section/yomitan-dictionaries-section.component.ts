import {ChangeDetectionStrategy, Component, computed, DestroyRef, inject, signal} from '@angular/core';
import {TranslocoDirective} from "@jsverse/transloco";
import {SettingItemComponent} from "../../../../settings/_components/setting-item/setting-item.component";
import {YomitanControlComponent} from "../yomitan-control/yomitan-control.component";
import {YomitanHelpComponent} from "../yomitan-help/yomitan-help.component";
import {YomitanDictionaryEntry, YomitanSettingsService} from "../../_services/yomitan-settings.service";
import {YomitanControlDef, YomitanDictionaryInfo} from "../../_models/yomitan-options";
import {COLLAPSIBLE_OPTIONS} from "../yomitan-appearance-section/yomitan-appearance-section.component";

/** How often the server's import progress is checked while an import runs */
const IMPORT_POLL_MS = 3000;

/** Rows of Yomitan's dictionary details dialog, in its order */
const DETAIL_ROWS: {property: string, key: string}[] = [
  {property: 'author', key: 'dictionary-details-author'},
  {property: 'url', key: 'dictionary-details-url'},
  {property: 'description', key: 'dictionary-details-description'},
  {property: 'attribution', key: 'dictionary-details-attribution'},
  {property: 'sourceLanguage', key: 'dictionary-details-source-language'},
  {property: 'targetLanguage', key: 'dictionary-details-target-language'},
  {property: 'terms', key: 'dictionary-details-terms'},
  {property: 'termMeta', key: 'dictionary-details-term-meta'},
  {property: 'kanji', key: 'dictionary-details-kanji'},
  {property: 'kanjiMeta', key: 'dictionary-details-kanji-meta'},
  {property: 'tagMeta', key: 'dictionary-details-tag-meta'},
  {property: 'media', key: 'dictionary-details-media'},
  {property: 'frequencyMode', key: 'dictionary-details-frequency-mode'},
  {property: 'prefixWildcardsSupported', key: 'dictionary-details-prefix-wildcards'},
  {property: 'importSuccess', key: 'dictionary-details-import-success'},
];

/** Yomitan's sample words for detecting whether a frequency dictionary ranks or counts */
const MORE_COMMON_TERMS = ['来る', '言う', '出る', '入る', '方', '男', '女', '今', '何', '時'];
const LESS_COMMON_TERMS = ['行なう', '論じる', '過す', '行方', '人口', '猫', '犬', '滝', '理', '暁'];

/**
 * What api/yomitan/dictionaries/import-status returns. Read defensively, the state may be the enum
 * name or its number.
 */
interface ImportStatus {
  state?: number | string;
  currentDictionary?: string | null;
  currentTitle?: string | null;
  banksDone?: number;
  banksTotal?: number;
  dictionariesDone?: number;
  dictionariesTotal?: number;
  errors?: string[];
}

/**
 * Yomitan's Dictionaries section, with the dictionaries dialog and each dictionary's details shown
 * inline. Kavita bundles its dictionaries on the server, so import, update and delete are left out.
 */
@Component({
  selector: 'app-yomitan-dictionaries-section',
  imports: [
    TranslocoDirective,
    SettingItemComponent,
    YomitanControlComponent,
    YomitanHelpComponent
  ],
  templateUrl: './yomitan-dictionaries-section.component.html',
  styleUrl: './yomitan-dictionaries-section.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class YomitanDictionariesSectionComponent {

  protected readonly settingsService = inject(YomitanSettingsService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly entries = this.settingsService.dictionaryEntries;
  protected readonly installedCount = computed(() => this.settingsService.dictionaries().length);
  protected readonly enabledCount = computed(() => this.entries().filter(e => e.info !== null && e.settings.enabled).length);
  protected readonly allEnabled = computed(() => this.entries().length > 0 && this.entries().every(e => e.settings.enabled));
  protected readonly expanded = signal<Set<string>>(new Set());
  protected readonly importStatus = signal<ImportStatus | null>(null);
  protected readonly importing = computed(() => isImporting(this.importStatus()));
  protected readonly importProgress = computed(() => {
    const status = this.importStatus();
    if (!status?.banksTotal) return 0;
    return Math.min(100, Math.round(100 * (status.banksDone ?? 0) / status.banksTotal));
  });

  protected readonly entryControls = computed(() => this.entries().map(entry => ({
    entry,
    details: getDetails(entry.info),
    hasTerms: (entry.info?.counts?.terms?.total ?? 0) > 0,
    aliasControl: {path: ['dictionaries', entry.index, 'alias'], type: 'text'} as YomitanControlDef,
    secondaryControl: {path: ['dictionaries', entry.index, 'allowSecondarySearches'], type: 'toggle'} as YomitanControlDef,
    collapsibleControl: {path: ['dictionaries', entry.index, 'definitionsCollapsible'], type: 'select', options: COLLAPSIBLE_OPTIONS} as YomitanControlDef,
    partsOfSpeechControl: {path: ['dictionaries', entry.index, 'partsOfSpeechFilter'], type: 'toggle'} as YomitanControlDef,
    deinflectionsControl: {path: ['dictionaries', entry.index, 'useDeinflections'], type: 'toggle'} as YomitanControlDef,
  })));

  /** Frequency dictionaries, the ones Yomitan offers for frequency sorting */
  protected readonly frequencyDictionaries = computed(() => this.settingsService.dictionaries()
    .filter(d => (d.counts?.termMeta?.['freq'] ?? 0) > 0));
  protected readonly sortFrequencyDictionary = computed<string | null>(() => this.settingsService.profileOptions()?.general['sortFrequencyDictionary'] ?? null);
  protected readonly sortFrequencyDictionaryOrder = computed<string>(() => this.settingsService.profileOptions()?.general['sortFrequencyDictionaryOrder'] ?? 'descending');

  private pollTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    void this.pollImportStatus();
    this.destroyRef.onDestroy(() => {
      if (this.pollTimer !== null) clearTimeout(this.pollTimer);
    });
  }

  toggleExpanded(name: string) {
    this.expanded.update(set => {
      const next = new Set(set);
      if (next.has(name)) {
        next.delete(name);
      } else {
        next.add(name);
      }
      return next;
    });
  }

  setEnabled(entry: YomitanDictionaryEntry, event: Event) {
    void this.settingsService.set(['dictionaries', entry.index, 'enabled'], (event.target as HTMLInputElement).checked);
  }

  setAllEnabled(event: Event) {
    const enabled = (event.target as HTMLInputElement).checked;
    void this.settingsService.modifyProfile(this.entries().map(entry => ({
      action: 'set' as const,
      path: ['dictionaries', entry.index, 'enabled'],
      value: enabled,
    })));
  }

  /**
   * Dictionary order is result priority. Moves swap neighbours, like Yomitan's up and down buttons.
   */
  move(entry: YomitanDictionaryEntry, offset: number) {
    const target = entry.index + offset;
    if (target < 0 || target >= this.entries().length) return;
    void this.settingsService.modifyProfile([{action: 'swap', path1: ['dictionaries', entry.index], path2: ['dictionaries', target]}]);
  }

  resetAlias(entry: YomitanDictionaryEntry) {
    void this.settingsService.set(['dictionaries', entry.index, 'alias'], entry.settings.name);
  }

  async setSortFrequencyDictionary(event: Event) {
    const value = (event.target as HTMLSelectElement).value;
    await this.settingsService.set('general.sortFrequencyDictionary', value === '' ? null : value);
    if (value !== '') await this.autoUpdateOrder(value);
  }

  setSortFrequencyDictionaryOrder(event: Event) {
    void this.settingsService.set('general.sortFrequencyDictionaryOrder', (event.target as HTMLSelectElement).value);
  }

  async autoUpdateOrder(dictionary: string | null = this.sortFrequencyDictionary()) {
    if (dictionary === null) return;
    const order = await this.getFrequencyOrder(dictionary);
    if (order === null) return;
    await this.settingsService.set('general.sortFrequencyDictionaryOrder', order);
  }

  /**
   * Yomitan's SortFrequencyDictionaryController detection: use the dictionary's declared mode, else
   * compare the values it gives common and uncommon Japanese words.
   */
  private async getFrequencyOrder(dictionary: string) {
    const info = this.settingsService.dictionaries().find(d => d.title === dictionary);
    switch (info?.frequencyMode) {
      case 'occurrence-based':
        return 'descending';
      case 'rank-based':
        return 'ascending';
    }

    let frequencies: {term: string, frequency: number}[];
    try {
      frequencies = await this.settingsService.invoke('getTermFrequencies', {
        termReadingList: [...MORE_COMMON_TERMS, ...LESS_COMMON_TERMS].map(term => ({term, reading: null})),
        dictionaries: [dictionary],
      });
    } catch (e) {
      return null;
    }

    const details = new Map<string, {hasValue: boolean, minValue: number, maxValue: number}>();
    for (const term of [...MORE_COMMON_TERMS, ...LESS_COMMON_TERMS]) {
      details.set(term, {hasValue: false, minValue: Number.MAX_SAFE_INTEGER, maxValue: Number.MIN_SAFE_INTEGER});
    }
    for (const {term, frequency} of frequencies ?? []) {
      const termDetails = details.get(term);
      if (termDetails === undefined) continue;
      termDetails.minValue = Math.min(termDetails.minValue, frequency);
      termDetails.maxValue = Math.max(termDetails.maxValue, frequency);
      termDetails.hasValue = true;
    }

    let result = 0;
    for (const term1 of MORE_COMMON_TERMS) {
      const details1 = details.get(term1)!;
      if (!details1.hasValue) continue;
      for (const term2 of LESS_COMMON_TERMS) {
        const details2 = details.get(term2)!;
        if (!details2.hasValue) continue;
        result += Math.sign(details1.maxValue - details2.minValue) + Math.sign(details1.minValue - details2.maxValue);
      }
    }

    const sign = Math.sign(result);
    if (sign > 0) return 'descending';
    if (sign < 0) return 'ascending';
    return null;
  }

  private async pollImportStatus() {
    let status: ImportStatus | null = null;
    try {
      status = await this.settingsService.request<ImportStatus>('dictionaries/import-status');
    } catch (e) {
      // The page works without import progress
      return;
    }

    const wasImporting = this.importing();
    this.importStatus.set(status && typeof status === 'object' ? status : null);

    if (wasImporting && !this.importing()) {
      await this.settingsService.reloadDictionaries();
    }
    if (this.importing()) {
      this.pollTimer = setTimeout(() => this.pollImportStatus(), IMPORT_POLL_MS);
    }
  }
}

function isImporting(status: ImportStatus | null) {
  if (status === null) return false;
  const state = status.state;
  return state === 1 || (typeof state === 'string' && state.toLowerCase() === 'importing');
}

/**
 * The rows Yomitan's details dialog shows: strings as is, counts when non-zero, booleans as text.
 */
function getDetails(info: YomitanDictionaryInfo | null) {
  if (info === null) return [];
  const values: Record<string, unknown> = {...info, ...(info.counts ?? {})};
  const rows: {key: string, value: string, isUrl: boolean}[] = [];
  for (const {property, key} of DETAIL_ROWS) {
    const value = values[property];
    let text: string | null = null;
    if (typeof value === 'string' && value.length > 0) {
      text = value;
    } else if (value !== null && typeof value === 'object' && 'total' in value) {
      const total = (value as {total: number}).total;
      text = total ? `${total}` : null;
    } else if (typeof value === 'boolean') {
      text = `${value}`;
    }
    if (text !== null) rows.push({key, value: text, isUrl: property === 'url'});
  }
  return rows;
}
