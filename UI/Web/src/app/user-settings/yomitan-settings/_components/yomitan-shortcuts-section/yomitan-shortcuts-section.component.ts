import {ChangeDetectionStrategy, Component, computed, inject} from '@angular/core';
import {translate, TranslocoDirective} from "@jsverse/transloco";
import {YomitanInputChange, YomitanInputFieldComponent} from "../yomitan-input-field/yomitan-input-field.component";
import {YomitanSettingsService} from "../../_services/yomitan-settings.service";
import {YomitanHotkey, YomitanModification, YomitanSelectOption} from "../../_models/yomitan-options";
import {ConfirmService} from "../../../../shared/confirm.service";

type ArgumentType = 'move-offset' | 'setting-path' | 'audio-source' | 'anki-card-format';

interface ActionDetails {
  scopes: string[];
  argument?: {type: ArgumentType, default: string};
}

/** Yomitan's KeyboardShortcutController._actionDetails, in the action select's order */
const ACTIONS: [string, ActionDetails][] = [
  ['', {scopes: []}],
  ['close', {scopes: ['popup', 'search']}],
  ['focusSearchBox', {scopes: ['search']}],
  ['nextEntry', {scopes: ['popup', 'search'], argument: {type: 'move-offset', default: '1'}}],
  ['previousEntry', {scopes: ['popup', 'search'], argument: {type: 'move-offset', default: '1'}}],
  ['lastEntry', {scopes: ['popup', 'search']}],
  ['firstEntry', {scopes: ['popup', 'search']}],
  ['nextEntryDifferentDictionary', {scopes: ['popup', 'search']}],
  ['previousEntryDifferentDictionary', {scopes: ['popup', 'search']}],
  ['historyBackward', {scopes: ['popup', 'search']}],
  ['historyForward', {scopes: ['popup', 'search']}],
  ['profilePrevious', {scopes: ['popup', 'search', 'web']}],
  ['profileNext', {scopes: ['popup', 'search', 'web']}],
  ['addNote', {scopes: ['popup', 'search'], argument: {type: 'anki-card-format', default: '0'}}],
  ['viewNotes', {scopes: ['popup', 'search'], argument: {type: 'anki-card-format', default: '0'}}],
  ['playAudio', {scopes: ['popup', 'search']}],
  ['playAudioFromSource', {scopes: ['popup', 'search'], argument: {type: 'audio-source', default: 'jpod101'}}],
  ['copyHostSelection', {scopes: ['popup']}],
  ['scanSelectedText', {scopes: ['web']}],
  ['scanTextAtSelection', {scopes: ['web']}],
  ['scanTextAtCaret', {scopes: ['web']}],
  ['toggleOption', {scopes: ['popup', 'search'], argument: {type: 'setting-path', default: ''}}],
];
const ACTION_DETAILS = new Map(ACTIONS);
const SCOPES = ['web', 'popup', 'search'];
const stringComparer = new Intl.Collator('en-US');

/** Yomitan's hotkey-argument-audio-source-template options */
const AUDIO_SOURCE_OPTIONS: YomitanSelectOption[] = [
  {value: 'jpod101', key: 'audio-source-jpod101'},
  {value: 'language-pod-101', key: 'audio-source-language-pod-101'},
  {value: 'jisho', key: 'audio-source-jisho'},
  {value: 'lingua-libre', key: 'hotkey-audio-source-lingua-libre'},
  {value: 'wiktionary', key: 'hotkey-audio-source-wiktionary'},
  {value: 'text-to-speech', key: 'audio-source-text-to-speech'},
  {value: 'text-to-speech-reading', key: 'audio-source-text-to-speech-reading'},
  {value: 'custom', key: 'hotkey-audio-source-custom'},
];

/**
 * Yomitan's Shortcuts section with the Keyboard Shortcuts dialog shown inline. Native browser
 * shortcuts are left out, Kavita isn't a browser extension.
 */
@Component({
  selector: 'app-yomitan-shortcuts-section',
  imports: [
    TranslocoDirective,
    YomitanInputFieldComponent
  ],
  templateUrl: './yomitan-shortcuts-section.component.html',
  styleUrl: './yomitan-shortcuts-section.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class YomitanShortcutsSectionComponent {

  private readonly settingsService = inject(YomitanSettingsService);
  private readonly confirmService = inject(ConfirmService);

  protected readonly actions = ACTIONS.map(([action]) => action);
  protected readonly audioSourceOptions = AUDIO_SOURCE_OPTIONS;
  protected readonly os = this.settingsService.os;
  protected readonly scopes = SCOPES;

  protected readonly cardFormatNames = computed(() => (this.settingsService.profileOptions()?.anki.cardFormats ?? []).map(f => f.name));
  protected readonly hotkeys = computed(() => {
    const hotkeys = this.settingsService.profileOptions()?.inputs.hotkeys ?? [];
    return hotkeys.map((hotkey, index) => {
      const details = ACTION_DETAILS.get(hotkey.action);
      return {
        index,
        hotkey,
        validScopes: details?.scopes ?? [],
        argumentType: details?.argument?.type ?? null,
      };
    });
  });

  actionLabelKey(action: string) {
    return action === '' ? 'hotkey-action-none' : `hotkey-action-${action.replace(/([a-z])([A-Z])/g, '$1-$2').toLowerCase()}`;
  }

  add() {
    const hotkey: YomitanHotkey = {action: '', argument: '', key: null, modifiers: [], scopes: ['popup', 'search'], enabled: true};
    void this.settingsService.modifyProfile([{action: 'push', path: 'inputs.hotkeys', items: [hotkey]}]);
  }

  remove(index: number) {
    void this.settingsService.modifyProfile([{action: 'splice', path: 'inputs.hotkeys', start: index, deleteCount: 1, items: []}]);
  }

  async resetAll() {
    if (!await this.confirmService.confirm(translate('yomitan-settings.hotkeys-reset-confirm'))) return;
    const defaults = await this.settingsService.getDefaultOptions();
    void this.settingsService.set('inputs.hotkeys', defaults.profiles[0].options.inputs.hotkeys);
  }

  setInput(index: number, change: YomitanInputChange) {
    void this.modifyHotkey(index, {key: change.key, modifiers: change.modifiers.filter(m => !m.startsWith('mouse'))});
  }

  clearInput(index: number) {
    void this.modifyHotkey(index, {key: null, modifiers: []});
  }

  async resetInput(hotkey: YomitanHotkey, index: number) {
    const defaults = await this.settingsService.getDefaultOptions();
    const match = defaults.profiles[0].options.inputs.hotkeys.find(h => h.action === hotkey.action);
    if (match === undefined) return;
    void this.modifyHotkey(index, {key: match.key, modifiers: match.modifiers});
  }

  resetArgument(hotkey: YomitanHotkey, index: number) {
    void this.modifyHotkey(index, {argument: ACTION_DETAILS.get(hotkey.action)?.argument?.default ?? ''});
  }

  setEnabled(index: number, event: Event) {
    void this.modifyHotkey(index, {enabled: (event.target as HTMLInputElement).checked});
  }

  setArgument(index: number, event: Event) {
    const element = event.target as HTMLInputElement | HTMLSelectElement;
    let value = element.value;
    // Like Yomitan's move offset argument, a count is stored as text but must be a whole number of at least 1
    if (element instanceof HTMLInputElement && element.type === 'number') {
      value = `${Math.max(1, Math.floor(Number(value) || 1))}`;
      element.value = value;
    }
    void this.modifyHotkey(index, {argument: value});
  }

  /**
   * Same as Yomitan's KeyboardShortcutHotkeyEntry._setScopeEnabled, scopes stay sorted.
   */
  setScope(hotkey: YomitanHotkey, index: number, scope: string, event: Event) {
    const enabled = (event.target as HTMLInputElement).checked;
    const scopes = hotkey.scopes.filter(s => s !== scope);
    if (enabled) {
      scopes.push(scope);
      scopes.sort((a, b) => stringComparer.compare(a, b));
    }
    void this.modifyHotkey(index, {scopes});
  }

  /**
   * Same as Yomitan's KeyboardShortcutHotkeyEntry._setAction: the argument resets to the new action's
   * default and scopes the new action can't use are dropped.
   */
  setAction(hotkey: YomitanHotkey, index: number, event: Event) {
    const action = (event.target as HTMLSelectElement).value;
    const oldValidScopes = ACTION_DETAILS.get(hotkey.action)?.scopes ?? [];
    const details = ACTION_DETAILS.get(action) ?? {scopes: []};
    const validScopes = details.scopes;

    let scopes = [...hotkey.scopes];
    let scopesChanged = false;
    if (oldValidScopes.length === scopes.length) {
      scopes = [];
      scopesChanged = true;
    } else {
      const filtered = scopes.filter(s => validScopes.includes(s));
      scopesChanged = filtered.length !== scopes.length;
      scopes = filtered;
    }
    if (scopesChanged && scopes.length === 0) {
      scopes.push(...validScopes);
    }

    void this.modifyHotkey(index, {action, argument: details.argument?.default ?? '', scopes});
  }


  private modifyHotkey(index: number, values: Partial<YomitanHotkey>) {
    const targets: YomitanModification[] = Object.entries(values).map(([property, value]) => ({
      action: 'set',
      path: ['inputs', 'hotkeys', index, property],
      value,
    }));
    return this.settingsService.modifyProfile(targets);
  }
}
