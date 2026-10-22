/*
 * Shapes of Yomitan's options document and backend responses, typed only as far as Kavita's settings
 * page reads them. The source of truth is Yomitan's options-schema.json.
 */

export type YomitanPathPart = string | number;
export type YomitanPath = string | YomitanPathPart[];

export interface YomitanOptions {
  version: number;
  profiles: YomitanProfile[];
  profileCurrent: number;
  global: {
    database: {prefixWildcardsSupported: boolean};
    [key: string]: any;
  };
}

export interface YomitanProfile {
  name: string;
  id?: string;
  conditionGroups: unknown[];
  options: YomitanProfileOptions;
}

export interface YomitanProfileOptions {
  general: {[key: string]: any};
  audio: {
    sources: YomitanAudioSource[];
    enableDefaultAudioSources: boolean;
    [key: string]: any;
  };
  scanning: {
    inputs: YomitanScanInput[];
    popupNestingMaxDepth: number;
    [key: string]: any;
  };
  translation: {
    searchResolution: string;
    textReplacements: {
      searchOriginal: boolean;
      groups: YomitanTextReplacement[][];
    };
  };
  dictionaries: YomitanDictionaryOptions[];
  parsing: {[key: string]: any};
  anki: YomitanAnkiOptions;
  sentenceParsing: {
    scanExtent: number;
    terminationCharacterMode: string;
    terminationCharacters: YomitanTerminationCharacter[];
  };
  inputs: {
    hotkeys: YomitanHotkey[];
  };
  [key: string]: any;
}

export interface YomitanAudioSource {
  type: string;
  url: string;
  voice: string;
}

export interface YomitanScanInput {
  include: string;
  exclude: string;
  types: {mouse: boolean, touch: boolean, pen: boolean};
  options: {
    showAdvanced: boolean;
    searchTerms: boolean;
    searchKanji: boolean;
    scanOnTouchTap: boolean;
    scanOnTouchMove: boolean;
    scanOnTouchPress: boolean;
    scanOnTouchRelease: boolean;
    scanOnPenMove: boolean;
    scanOnPenHover: boolean;
    scanOnPenReleaseHover: boolean;
    scanOnPenPress: boolean;
    scanOnPenRelease: boolean;
    preventTouchScrolling: boolean;
    preventPenScrolling: boolean;
    minimumTouchTime: number;
    [key: string]: boolean | number;
  };
}

export interface YomitanTextReplacement {
  pattern: string;
  ignoreCase: boolean;
  replacement: string;
}

export interface YomitanTerminationCharacter {
  enabled: boolean;
  character1: string;
  character2: string | null;
  includeCharacterAtStart: boolean;
  includeCharacterAtEnd: boolean;
}

export interface YomitanHotkey {
  action: string;
  argument: string;
  key: string | null;
  modifiers: string[];
  scopes: string[];
  enabled: boolean;
}

export interface YomitanDictionaryOptions {
  name: string;
  alias: string;
  enabled: boolean;
  allowSecondarySearches: boolean;
  definitionsCollapsible: string;
  partsOfSpeechFilter: boolean;
  useDeinflections: boolean;
  styles?: string;
}

export interface YomitanAnkiOptions {
  enable: boolean;
  server: string;
  apiKey: string;
  tags: string[];
  fieldTemplates: string | null;
  cardFormats: YomitanAnkiCardFormat[];
  [key: string]: any;
}

export interface YomitanAnkiCardFormat {
  name: string;
  type: 'term' | 'kanji';
  deck: string;
  model: string;
  fields: {[fieldName: string]: {value: string, overwriteMode: string}};
  icon: string;
}

/**
 * Yomitan's DictionarySummary, as returned by the getDictionaryInfo action
 */
export interface YomitanDictionaryInfo {
  title: string;
  revision: string;
  sequenced?: boolean;
  version: number;
  importDate?: number;
  prefixWildcardsSupported?: boolean;
  importSuccess?: boolean;
  author?: string;
  url?: string;
  description?: string;
  attribution?: string;
  sourceLanguage?: string;
  targetLanguage?: string;
  frequencyMode?: string;
  counts?: {
    terms?: {total: number};
    termMeta?: {total: number, [mode: string]: number};
    kanji?: {total: number};
    kanjiMeta?: {total: number, [mode: string]: number};
    tagMeta?: {total: number};
    media?: {total: number};
  };
  styles?: string;
}

/**
 * A change in Yomitan's settings-modifications.d.ts shape. Paths are relative to the options root.
 */
export type YomitanModification =
  {action: 'set', path: YomitanPath, value: unknown} |
  {action: 'delete', path: YomitanPath} |
  {action: 'swap', path1: YomitanPath, path2: YomitanPath} |
  {action: 'splice', path: YomitanPath, start: number, deleteCount: number, items: unknown[]} |
  {action: 'push', path: YomitanPath, items: unknown[]};

export type YomitanControlType = 'toggle' | 'checkbox' | 'number' | 'select' | 'text' | 'password' | 'textarea';

export interface YomitanSelectOption {
  value: string;
  /** Translation key under yomitan-settings */
  key?: string;
  /** Untranslated label, for values that come from data such as dictionary titles */
  label?: string;
}

/**
 * One input bound to one Yomitan option.
 */
export interface YomitanControlDef {
  /** Relative to the current profile's options, or to the options root when global */
  path: YomitanPath;
  type: YomitanControlType;
  global?: boolean;
  /** Translation key of the label shown next to this control when a setting has several */
  sub?: string;
  min?: number;
  max?: number;
  step?: number;
  placeholder?: string;
  /** Fixed options, or the name of a list built from the installed dictionaries */
  options?: YomitanSelectOption[] | 'mainDictionary';
  /** The select's string value is stored as a number */
  numeric?: boolean;
  /** Stored as an array of tags and edited as space separated text */
  transform?: 'tags';
}

/**
 * One row of Yomitan's settings page. Text comes from en.json as `${key}-label`, `${key}-tooltip`
 * and `${key}-help`.
 */
export interface YomitanSettingDef {
  key: string;
  controls: YomitanControlDef[];
  /** Hidden until "Advanced" is on, like Yomitan's advanced-only class */
  advanced?: boolean;
  description?: boolean;
  help?: boolean;
  /** Indented beneath the setting it depends on */
  child?: boolean;
  visibleWhen?: (options: YomitanProfileOptions) => boolean;
}
