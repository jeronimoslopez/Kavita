import {ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked} from '@angular/core';
import {TranslocoDirective} from "@jsverse/transloco";
import {debounce, form, FormField} from "@angular/forms/signals";
import {SettingSwitchComponent} from "../../../../settings/_components/setting-switch/setting-switch.component";
import {YomitanSettingComponent} from "../yomitan-setting/yomitan-setting.component";
import {YomitanControlComponent} from "../yomitan-control/yomitan-control.component";
import {YomitanHelpComponent} from "../yomitan-help/yomitan-help.component";
import {YomitanSettingsService} from "../../_services/yomitan-settings.service";
import {YOMITAN_ANKI_SETTINGS} from "../../_models/yomitan-setting-definitions";
import {YomitanAnkiCardFormat, YomitanControlDef, YomitanSelectOption} from "../../_models/yomitan-options";

/** Yomitan caps the number of card formats at 5 */
const MAX_CARD_FORMATS = 5;
/** Waits for the server address to settle before reconnecting */
const RECONNECT_DEBOUNCE_MS = 600;

const CARD_TYPE_OPTIONS: YomitanSelectOption[] = [
  {value: 'term', key: 'anki-card-type-term'},
  {value: 'kanji', key: 'anki-card-type-kanji'},
];
const CARD_ICON_OPTIONS: YomitanSelectOption[] = [
  {value: 'big-circle', key: 'anki-card-icon-big-circle'},
  {value: 'small-circle', key: 'anki-card-icon-small-circle'},
  {value: 'big-square', key: 'anki-card-icon-big-square'},
  {value: 'big-diamond', key: 'anki-card-icon-big-diamond'},
];
const OVERWRITE_OPTIONS: YomitanSelectOption[] = [
  {value: 'skip', key: 'anki-overwrite-skip'},
  {value: 'prepend', key: 'anki-overwrite-prepend'},
  {value: 'append', key: 'anki-overwrite-append'},
  {value: 'coalesce', key: 'anki-overwrite-coalesce'},
  {value: 'coalesce-new', key: 'anki-overwrite-coalesce-new'},
  {value: 'overwrite', key: 'anki-overwrite-overwrite'},
];

/** Markers that need browser extension APIs Kavita doesn't have */
const UNSUPPORTED_MARKERS = new Set(['screenshot']);

/** Yomitan's AnkiCardController aliases for matching model field names to markers */
const MARKER_ALIASES = new Map<string, string[]>([
  ['expression', ['phrase', 'term', 'word']],
  ['reading', ['expression-reading', 'term-reading', 'word-reading']],
  ['furigana', ['expression-furigana', 'term-furigana', 'word-furigana']],
  ['glossary', ['definition', 'meaning']],
  ['audio', ['sound', 'word-audio', 'term-audio', 'expression-audio']],
  ['dictionary', ['dict']],
  ['pitch-accents', ['pitch', 'pitch-accent', 'pitch-pattern']],
  ['sentence', ['example-sentence']],
  ['frequency-harmonic-rank', ['freq', 'frequency', 'freq-sort', 'freqency-sort']],
  // Yomitan lists this marker twice, and its Map keeps the later entry
  ['popup-selection-text', ['selection-text']],
  ['pitch-accent-positions', ['pitch-position']],
  ['pitch-accent-categories', ['pitch-categories']],
]);

interface AnkiTemplateUtil {
  getStandardFieldMarkers(type: string, language?: string): string[];
  getDynamicFieldMarkers(dictionaries: unknown[], dictionaryInfo: unknown[]): string[];
}

type ConnectionState =
  {status: 'disabled'} |
  {status: 'checking'} |
  {status: 'connected', version: number} |
  {status: 'error', message: string};

/**
 * Yomitan's Anki section, with the Anki Cards and Card Templates dialogs shown inline. AnkiConnect is
 * called straight from the browser, so it has to allow Kavita's origin. The screenshot format and the
 * note generator are left out, they need browser extension APIs.
 */
@Component({
  selector: 'app-yomitan-anki-section',
  imports: [
    TranslocoDirective,
    FormField,
    SettingSwitchComponent,
    YomitanSettingComponent,
    YomitanControlComponent,
    YomitanHelpComponent
  ],
  templateUrl: './yomitan-anki-section.component.html',
  styleUrl: './yomitan-anki-section.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class YomitanAnkiSectionComponent {

  protected readonly settingsService = inject(YomitanSettingsService);

  protected readonly settings = YOMITAN_ANKI_SETTINGS;
  protected readonly enableControl: YomitanControlDef = {path: 'anki.enable', type: 'toggle'};
  protected readonly origin = window.location.origin;
  protected readonly corsConfig = JSON.stringify({webCorsOriginList: ['http://localhost', window.location.origin]}, null, 2);
  protected readonly maxCardFormats = MAX_CARD_FORMATS;
  protected readonly overwriteOptions = OVERWRITE_OPTIONS;

  protected readonly anki = computed(() => this.settingsService.profileOptions()?.anki ?? null);
  protected readonly connection = signal<ConnectionState>({status: 'disabled'});
  protected readonly deckNames = signal<string[]>([]);
  protected readonly modelNames = signal<string[]>([]);
  protected readonly selectedFormat = signal(0);
  /** Markers offered in each field's menu, and the standard ones used to guess a new model's fields */
  protected readonly markers = signal({term: [] as string[], kanji: [] as string[], standardTerm: [] as string[], standardKanji: [] as string[]});

  protected readonly cardFormats = computed(() => this.anki()?.cardFormats ?? []);
  protected readonly format = computed(() => {
    const formats = this.cardFormats();
    return formats[Math.min(this.selectedFormat(), formats.length - 1)] ?? null;
  });
  protected readonly formatIndex = computed(() => Math.min(this.selectedFormat(), Math.max(0, this.cardFormats().length - 1)));
  protected readonly formatControls = computed(() => {
    const index = this.formatIndex();
    const base = ['anki', 'cardFormats', index];
    return {
      name: {path: [...base, 'name'], type: 'text'} as YomitanControlDef,
      type: {path: [...base, 'type'], type: 'select', options: CARD_TYPE_OPTIONS} as YomitanControlDef,
      icon: {path: [...base, 'icon'], type: 'select', options: CARD_ICON_OPTIONS} as YomitanControlDef,
    };
  });
  protected readonly fields = computed(() => {
    const format = this.format();
    if (format === null) return [];
    const index = this.formatIndex();
    return Object.entries(format.fields).map(([name, field]) => ({
      name,
      field,
      valueControl: {path: ['anki', 'cardFormats', index, 'fields', name, 'value'], type: 'text'} as YomitanControlDef,
      overwriteControl: {path: ['anki', 'cardFormats', index, 'fields', name, 'overwriteMode'], type: 'select', options: OVERWRITE_OPTIONS} as YomitanControlDef,
    }));
  });
  protected readonly formatMarkers = computed(() => {
    const format = this.format();
    if (format === null) return [];
    return format.type === 'kanji' ? this.markers().kanji : this.markers().term;
  });
  /** Deck and model lists, keeping the saved value selectable while AnkiConnect is unreachable */
  protected readonly deckOptions = computed(() => withCurrent(this.deckNames(), this.format()?.deck));
  protected readonly modelOptions = computed(() => withCurrent(this.modelNames(), this.format()?.model));

  protected readonly defaultTemplates = signal<string | null>(null);
  protected readonly templatesModel = signal({value: ''});
  protected readonly templatesForm = form(this.templatesModel, (path) => {
    debounce(path.value, 400);
  });

  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private modelChangingTo: string | null = null;

  constructor() {
    void this.loadMarkers();
    void this.loadDefaultTemplates();

    // Reconnect when the server, key or enabled state changes
    effect(() => {
      const anki = this.anki();
      const key = anki === null ? '' : `${anki.enable}|${anki.server}|${anki.apiKey}`;
      untracked(() => {
        if (this.reconnectTimer !== null) clearTimeout(this.reconnectTimer);
        if (key === '') return;
        this.reconnectTimer = setTimeout(() => this.refresh(), RECONNECT_DEBOUNCE_MS);
      });
    });

    // Field templates follow Yomitan's rule: null means the default templates
    effect(() => {
      const stored = this.anki()?.fieldTemplates ?? null;
      const defaults = this.defaultTemplates();
      untracked(() => {
        const text = stored ?? defaults ?? '';
        if (this.templatesModel().value !== text) this.templatesModel.set({value: text});
      });
    });
    effect(() => {
      const text = this.templatesModel().value;
      untracked(() => {
        const defaults = this.defaultTemplates();
        if (defaults === null) return;
        const value = text === defaults ? null : text;
        if (value === (this.anki()?.fieldTemplates ?? null)) return;
        void this.settingsService.set('anki.fieldTemplates', value);
      });
    });
  }

  async refresh() {
    const anki = this.anki();
    if (anki === null || !anki.enable) {
      this.connection.set({status: 'disabled'});
      return;
    }

    this.connection.set({status: 'checking'});
    try {
      const version = await this.ankiInvoke<number>('version');
      const [decks, models] = await Promise.all([
        this.ankiInvoke<string[]>('deckNames'),
        this.ankiInvoke<string[]>('modelNames'),
      ]);
      this.deckNames.set([...decks].sort());
      this.modelNames.set([...models].sort());
      this.connection.set({status: 'connected', version});
    } catch (e) {
      this.connection.set({status: 'error', message: e instanceof Error ? e.message : `${e}`});
    }
  }

  addFormat() {
    const formats = this.cardFormats();
    if (formats.length >= MAX_CARD_FORMATS) return;
    const format: YomitanAnkiCardFormat = {
      name: `Format ${formats.length + 1}`,
      type: 'term',
      deck: '',
      model: '',
      fields: {},
      icon: 'big-circle',
    };
    void this.settingsService.modifyProfile([{action: 'push', path: 'anki.cardFormats', items: [format]}]);
    this.selectedFormat.set(formats.length);
  }

  deleteFormat() {
    const formats = this.cardFormats();
    if (formats.length <= 1) return;
    const index = this.formatIndex();
    void this.settingsService.modifyProfile([{action: 'splice', path: 'anki.cardFormats', start: index, deleteCount: 1, items: []}]);
    this.selectedFormat.set(Math.max(0, index - 1));
  }

  setDeck(event: Event) {
    void this.settingsService.set(['anki', 'cardFormats', this.formatIndex(), 'deck'], (event.target as HTMLSelectElement).value);
  }

  /**
   * Same as Yomitan's AnkiCardController._setModel: fetch the model's fields and fill each one with
   * the old value or the marker its name suggests.
   */
  async setModel(event: Event) {
    const select = event.target as HTMLSelectElement;
    const model = select.value;
    const format = this.format();
    if (format === null || this.modelChangingTo !== null || model === format.model) return;

    const index = this.formatIndex();
    let fieldNames: string[];
    try {
      this.modelChangingTo = model;
      fieldNames = await this.ankiInvoke<string[]>('modelFieldNames', {modelName: model});
    } catch (e) {
      select.value = format.model;
      this.connection.set({status: 'error', message: e instanceof Error ? e.message : `${e}`});
      return;
    } finally {
      this.modelChangingTo = null;
    }

    const standardMarkers = format.type === 'kanji' ? this.markers().standardKanji : this.markers().standardTerm;
    const fields: YomitanAnkiCardFormat['fields'] = {};
    fieldNames.forEach((fieldName, i) => {
      fields[fieldName] = {
        value: getDefaultFieldValue(fieldName, i, format.type, format.fields, standardMarkers),
        overwriteMode: 'coalesce',
      };
    });

    await this.settingsService.modifyProfile([
      {action: 'set', path: ['anki', 'cardFormats', index, 'model'], value: model},
      {action: 'set', path: ['anki', 'cardFormats', index, 'fields'], value: fields},
    ]);
  }

  setFieldMarker(fieldName: string, event: Event) {
    const select = event.target as HTMLSelectElement;
    const marker = select.value;
    select.value = '';
    if (marker === '') return;
    void this.settingsService.set(['anki', 'cardFormats', this.formatIndex(), 'fields', fieldName, 'value'], `{${marker}}`);
  }

  resetTemplates() {
    void this.settingsService.set('anki.fieldTemplates', null);
  }

  private async ankiInvoke<T>(action: string, params: object = {}): Promise<T> {
    const anki = this.anki();
    if (anki === null || !anki.server) throw new Error('Server URL is null');

    const body: Record<string, unknown> = {action, params, version: 6};
    if (anki.apiKey) body['key'] = anki.apiKey;

    let response: Response;
    try {
      response = await fetch(anki.server, {
        method: 'POST',
        mode: 'cors',
        cache: 'default',
        credentials: 'omit',
        headers: {'Content-Type': 'application/json'},
        redirect: 'follow',
        referrerPolicy: 'no-referrer',
        body: JSON.stringify(body),
      });
    } catch (e) {
      throw new Error('Anki connection failure');
    }
    if (!response.ok) throw new Error(`Anki connection error: ${response.status}`);

    const result = await response.json();
    if (result !== null && typeof result === 'object' && 'error' in result) {
      if (result.error) throw new Error(`${result.error}`);
      return result.result as T;
    }
    return result as T;
  }

  private async loadMarkers() {
    try {
      const util = await this.settingsService.importYomitanModule<AnkiTemplateUtil>('js/data/anki-template-util.js');
      const dynamic = util.getDynamicFieldMarkers(this.settingsService.profileOptions()?.dictionaries ?? [], this.settingsService.dictionaries());
      const clean = (markers: string[]) => [...new Set(markers.filter(m => !UNSUPPORTED_MARKERS.has(m)).sort())];
      const standardTerm = util.getStandardFieldMarkers('term', 'ja');
      const standardKanji = util.getStandardFieldMarkers('kanji', 'ja');
      this.markers.set({
        term: clean([...standardTerm, ...dynamic]),
        kanji: clean(standardKanji),
        standardTerm,
        standardKanji,
      });
    } catch (e) {
      console.error('[Yomitan] could not load Anki field markers', e);
    }
  }

  private async loadDefaultTemplates() {
    try {
      this.defaultTemplates.set(await this.settingsService.invoke<string>('getDefaultAnkiFieldTemplates'));
    } catch (e) {
      console.error('[Yomitan] could not load the default Anki templates', e);
    }
  }
}

function withCurrent(names: string[], current: string | undefined) {
  if (!current || names.includes(current)) return names;
  return [current, ...names];
}

/**
 * Yomitan's AnkiCardController._getDefaultFieldValue.
 */
function getDefaultFieldValue(fieldName: string, index: number, type: string, oldFields: YomitanAnkiCardFormat['fields'] | null, markers: string[]) {
  if (oldFields && Object.prototype.hasOwnProperty.call(oldFields, fieldName)) {
    return oldFields[fieldName].value;
  }
  if (index === 0) {
    return type === 'kanji' ? '{character}' : '{expression}';
  }

  for (const marker of markers) {
    const names = [marker, ...(MARKER_ALIASES.get(marker) ?? [])];
    const pattern = new RegExp(`^(?:${names.map(name => name.replace(/-/g, '[-_ ]*')).join('|')})$`, 'i');
    if (pattern.test(fieldName)) return `{${marker}}`;
  }
  return '';
}
