import {ChangeDetectionStrategy, Component, computed, DestroyRef, inject, signal} from '@angular/core';
import {TranslocoDirective} from "@jsverse/transloco";
import {NgTemplateOutlet} from "@angular/common";
import {YomitanSettingComponent} from "../yomitan-setting/yomitan-setting.component";
import {YomitanControlComponent} from "../yomitan-control/yomitan-control.component";
import {YomitanHelpComponent} from "../yomitan-help/yomitan-help.component";
import {YomitanSettingsService} from "../../_services/yomitan-settings.service";
import {YOMITAN_AUDIO_SETTINGS} from "../../_models/yomitan-setting-definitions";
import {YomitanAudioSource, YomitanControlDef, YomitanSelectOption} from "../../_models/yomitan-options";

/** Yomitan's audio-source-template options. Japanese adds JapanesePod101 and Jisho to the general list. */
const AUDIO_SOURCE_TYPES: YomitanSelectOption[] = [
  {value: 'jpod101', key: 'audio-source-jpod101'},
  {value: 'language-pod-101', key: 'audio-source-language-pod-101'},
  {value: 'jisho', key: 'audio-source-jisho'},
  {value: 'lingua-libre', key: 'audio-source-lingua-libre'},
  {value: 'wiktionary', key: 'audio-source-wiktionary'},
  {value: 'text-to-speech', key: 'audio-source-text-to-speech'},
  {value: 'text-to-speech-reading', key: 'audio-source-text-to-speech-reading'},
  {value: 'custom', key: 'audio-source-custom'},
  {value: 'custom-json', key: 'audio-source-custom-json'},
];
const AVAILABLE_TYPES = ['jpod101', 'jisho', 'language-pod-101', 'lingua-libre', 'wiktionary', 'text-to-speech', 'custom'];

interface VoiceOption {
  value: string;
  label: string;
}

/**
 * Yomitan's Audio section, with the Audio Sources dialog shown inline.
 */
@Component({
  selector: 'app-yomitan-audio-section',
  imports: [
    TranslocoDirective,
    NgTemplateOutlet,
    YomitanSettingComponent,
    YomitanControlComponent,
    YomitanHelpComponent
  ],
  templateUrl: './yomitan-audio-section.component.html',
  styleUrl: './yomitan-audio-section.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class YomitanAudioSectionComponent {

  private readonly settingsService = inject(YomitanSettingsService);

  protected readonly settings = YOMITAN_AUDIO_SETTINGS.slice(0, 4);
  protected readonly defaultSourcesSetting = YOMITAN_AUDIO_SETTINGS[4];
  protected readonly sourceTypes = AUDIO_SOURCE_TYPES;
  protected readonly voices = signal<VoiceOption[]>([]);
  protected readonly testText = signal('よみたん');

  protected readonly sources = computed(() => {
    const sources = this.settingsService.profileOptions()?.audio.sources ?? [];
    return sources.map((source, index) => ({
      index,
      source,
      typeControl: {path: ['audio', 'sources', index, 'type'], type: 'select', options: AUDIO_SOURCE_TYPES} as YomitanControlDef,
      urlControl: {path: ['audio', 'sources', index, 'url'], type: 'text'} as YomitanControlDef,
    }));
  });

  constructor() {
    if (typeof speechSynthesis === 'undefined') return;
    const update = () => this.voices.set(this.getVoices());
    update();
    speechSynthesis.addEventListener('voiceschanged', update);
    inject(DestroyRef).onDestroy(() => speechSynthesis.removeEventListener('voiceschanged', update));
  }

  parameterField(type: string) {
    switch (type) {
      case 'custom':
      case 'custom-json':
        return 'url';
      case 'text-to-speech':
      case 'text-to-speech-reading':
        return 'voice';
      default:
        return null;
    }
  }

  add() {
    const used = new Set((this.settingsService.profileOptions()?.audio.sources ?? []).map(s => s.type));
    const type = AVAILABLE_TYPES.find(t => !used.has(t)) ?? AVAILABLE_TYPES[0];
    const source: YomitanAudioSource = {type, url: '', voice: ''};
    void this.settingsService.modifyProfile([{action: 'push', path: 'audio.sources', items: [source]}]);
  }

  remove(index: number) {
    void this.settingsService.modifyProfile([{action: 'splice', path: 'audio.sources', start: index, deleteCount: 1, items: []}]);
  }

  move(index: number, offset: number) {
    const target = index + offset;
    if (target < 0 || target >= this.sources().length) return;
    void this.settingsService.modifyProfile([{action: 'swap', path1: ['audio', 'sources', index], path2: ['audio', 'sources', target]}]);
  }

  setVoice(index: number, event: Event) {
    void this.settingsService.set(['audio', 'sources', index, 'voice'], (event.target as HTMLSelectElement).value);
  }

  testVoice(voiceUri: string) {
    if (typeof speechSynthesis === 'undefined') return;
    const utterance = new SpeechSynthesisUtterance(this.testText());
    utterance.lang = 'ja-JP';
    const voice = speechSynthesis.getVoices().find(v => v.voiceURI === voiceUri);
    if (voice) utterance.voice = voice;
    speechSynthesis.cancel();
    speechSynthesis.speak(utterance);
  }

  /**
   * Japanese voices first, then the system default, like Yomitan's AudioController.
   */
  private getVoices(): VoiceOption[] {
    const isJapanese = (lang: string) => lang.startsWith('ja_') || lang.startsWith('ja-') || lang.startsWith('jpn-');
    return speechSynthesis.getVoices()
      .map((voice, index) => ({voice, index, japanese: isJapanese(voice.lang)}))
      .sort((a, b) => (Number(b.japanese) - Number(a.japanese)) || (Number(b.voice.default) - Number(a.voice.default)) || (a.index - b.index))
      .map(({voice}) => ({value: voice.voiceURI, label: `${voice.name} (${voice.lang})`}));
  }
}
