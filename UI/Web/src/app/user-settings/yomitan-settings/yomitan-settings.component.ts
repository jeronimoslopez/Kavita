import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  ElementRef,
  inject,
  signal
} from '@angular/core';
import {TranslocoDirective} from "@jsverse/transloco";
import {LoadingComponent} from "../../shared/loading/loading.component";
import {SettingSwitchComponent} from "../../settings/_components/setting-switch/setting-switch.component";
import {YomitanSettingsService} from "./_services/yomitan-settings.service";
import {YomitanControlComponent} from "./_components/yomitan-control/yomitan-control.component";
import {YomitanControlDef} from "./_models/yomitan-options";
import {YomitanProfileSectionComponent} from "./_components/yomitan-profile-section/yomitan-profile-section.component";
import {YomitanGeneralSectionComponent} from "./_components/yomitan-general-section/yomitan-general-section.component";
import {
  YomitanDictionariesSectionComponent
} from "./_components/yomitan-dictionaries-section/yomitan-dictionaries-section.component";
import {YomitanScanningSectionComponent} from "./_components/yomitan-scanning-section/yomitan-scanning-section.component";
import {YomitanPopupSectionComponent} from "./_components/yomitan-popup-section/yomitan-popup-section.component";
import {
  YomitanAppearanceSectionComponent
} from "./_components/yomitan-appearance-section/yomitan-appearance-section.component";
import {
  YomitanResultDisplaySectionComponent
} from "./_components/yomitan-result-display-section/yomitan-result-display-section.component";
import {YomitanPopupSizeSectionComponent} from "./_components/yomitan-popup-size-section/yomitan-popup-size-section.component";
import {YomitanAudioSectionComponent} from "./_components/yomitan-audio-section/yomitan-audio-section.component";
import {
  YomitanTextParsingSectionComponent
} from "./_components/yomitan-text-parsing-section/yomitan-text-parsing-section.component";
import {
  YomitanTranslationSectionComponent
} from "./_components/yomitan-translation-section/yomitan-translation-section.component";
import {YomitanAnkiSectionComponent} from "./_components/yomitan-anki-section/yomitan-anki-section.component";
import {YomitanShortcutsSectionComponent} from "./_components/yomitan-shortcuts-section/yomitan-shortcuts-section.component";
import {YomitanBackupSectionComponent} from "./_components/yomitan-backup-section/yomitan-backup-section.component";

export enum YomitanSectionId {
  Profile = 'profile',
  General = 'general',
  Dictionaries = 'dictionaries',
  Scanning = 'scanning',
  Popup = 'popup',
  Appearance = 'appearance',
  ResultDisplay = 'result-display',
  PopupSize = 'popup-size',
  Audio = 'audio',
  TextParsing = 'text-parsing',
  Translation = 'translation',
  Anki = 'anki',
  Shortcuts = 'shortcuts',
  Backup = 'backup',
}

interface YomitanSection {
  id: YomitanSectionId;
  /** Every setting kept from this section is an advanced one */
  advancedOnly?: boolean;
}

/** The sections kept from Yomitan's settings page, in its order */
const SECTIONS: YomitanSection[] = [
  {id: YomitanSectionId.Profile},
  {id: YomitanSectionId.General},
  {id: YomitanSectionId.Dictionaries},
  {id: YomitanSectionId.Scanning},
  {id: YomitanSectionId.Popup},
  {id: YomitanSectionId.Appearance},
  {id: YomitanSectionId.ResultDisplay},
  {id: YomitanSectionId.PopupSize},
  {id: YomitanSectionId.Audio},
  {id: YomitanSectionId.TextParsing, advancedOnly: true},
  {id: YomitanSectionId.Translation, advancedOnly: true},
  {id: YomitanSectionId.Anki},
  {id: YomitanSectionId.Shortcuts},
  {id: YomitanSectionId.Backup},
];

/**
 * Kavita's copy of Yomitan's settings page. Every change is saved through Yomitan's backend as soon as
 * it is made, which stores the options on Kavita's server.
 */
@Component({
  selector: 'app-yomitan-settings',
  imports: [
    TranslocoDirective,
    LoadingComponent,
    SettingSwitchComponent,
    YomitanControlComponent,
    YomitanProfileSectionComponent,
    YomitanGeneralSectionComponent,
    YomitanDictionariesSectionComponent,
    YomitanScanningSectionComponent,
    YomitanPopupSectionComponent,
    YomitanAppearanceSectionComponent,
    YomitanResultDisplaySectionComponent,
    YomitanPopupSizeSectionComponent,
    YomitanAudioSectionComponent,
    YomitanTextParsingSectionComponent,
    YomitanTranslationSectionComponent,
    YomitanAnkiSectionComponent,
    YomitanShortcutsSectionComponent,
    YomitanBackupSectionComponent
  ],
  providers: [YomitanSettingsService],
  templateUrl: './yomitan-settings.component.html',
  styleUrl: './yomitan-settings.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class YomitanSettingsComponent {

  protected readonly settingsService = inject(YomitanSettingsService);
  private readonly elementRef = inject(ElementRef<HTMLElement>);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly SectionId = YomitanSectionId;
  protected readonly loading = signal(true);
  protected readonly loadError = signal<string | null>(null);
  protected readonly activeSection = signal<YomitanSectionId>(YomitanSectionId.Profile);
  protected readonly advancedControl: YomitanControlDef = {path: 'general.showAdvanced', type: 'toggle'};

  protected readonly sections = computed(() => {
    const showAdvanced = this.settingsService.showAdvanced();
    return SECTIONS.filter(section => !section.advancedOnly || showAdvanced);
  });

  private observer: IntersectionObserver | null = null;

  constructor() {
    void this.load();

    afterNextRender(() => {
      this.observer = new IntersectionObserver(entries => this.onSectionsIntersect(entries), {
        rootMargin: '-20% 0px -70% 0px',
      });
    });
    this.destroyRef.onDestroy(() => this.observer?.disconnect());

    // Advanced mode adds and removes sections
    effect(() => {
      this.sections();
      requestAnimationFrame(() => this.observeSections());
    });
  }

  async load() {
    this.loading.set(true);
    this.loadError.set(null);
    try {
      await this.settingsService.load();
    } catch (e) {
      console.error('[Yomitan] could not load settings', e);
      this.loadError.set(e instanceof Error ? e.message : `${e}`);
    } finally {
      this.loading.set(false);
    }
  }

  /**
   * In-page navigation scrolls instead of using #anchors, because the settings page uses the URL
   * fragment to pick its tab.
   */
  scrollTo(id: YomitanSectionId) {
    const element = this.getSectionElement(id);
    if (!element) return;
    this.activeSection.set(id);
    element.scrollIntoView({behavior: 'smooth', block: 'start'});
  }

  observeSections() {
    if (this.observer === null) return;
    this.observer.disconnect();
    for (const section of this.sections()) {
      const element = this.getSectionElement(section.id);
      if (element) this.observer.observe(element);
    }
  }

  private onSectionsIntersect(entries: IntersectionObserverEntry[]) {
    const visible = entries.filter(e => e.isIntersecting);
    if (visible.length === 0) return;
    const id = (visible[0].target as HTMLElement).dataset['section'] as YomitanSectionId | undefined;
    if (id) this.activeSection.set(id);
  }

  private getSectionElement(id: YomitanSectionId) {
    return (this.elementRef.nativeElement as HTMLElement).querySelector<HTMLElement>(`[data-section="${id}"]`);
  }
}
