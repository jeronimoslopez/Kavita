import {
  ChangeDetectionStrategy,
  ChangeDetectorRef,
  Component,
  computed,
  DestroyRef,
  effect,
  ElementRef,
  HostListener,
  inject,
  OnDestroy,
  OnInit,
  signal,
  viewChild
} from '@angular/core';
import {ActivatedRoute, Router} from '@angular/router';
import {
  NgxExtendedPdfViewerModule,
  PageRenderedEvent,
  PageViewModeType,
  pdfDefaultOptions,
  PDFNotificationService,
  ProgressBarEvent,
  ScrollModeType,
  SpreadType
} from 'ngx-extended-pdf-viewer';
import {ToastrService} from '@openng/ngx-toastr';
import {filter, firstValueFrom, take} from 'rxjs';
import {NgbTooltip} from '@ng-bootstrap/ng-bootstrap';
import {DOCUMENT, NgStyle} from '@angular/common';
import {translate, TranslocoDirective} from "@jsverse/transloco";
import {PdfLayoutMode} from "../../../_models/preferences/pdf-layout-mode";
import {PdfScrollMode} from "../../../_models/preferences/pdf-scroll-mode";
import {PdfTheme} from "../../../_models/preferences/pdf-theme";
import {PdfSpreadMode} from "../../../_models/preferences/pdf-spread-mode";
import {PdfScrollModeTypePipe} from "../../_pipe/pdf-scroll-mode.pipe";
import {PdfSpreadTypePipe} from "../../_pipe/pdf-spread-mode.pipe";
import {ReadingProfile} from "../../../_models/preferences/reading-profiles";
import {takeUntilDestroyed} from "@angular/core/rxjs-interop";
import {KeyBindService} from "../../../_services/key-bind.service";
import {KeyBindTarget} from "../../../_models/preferences/preferences";
import {Breakpoint, BreakpointService} from "../../../_services/breakpoint.service";
import {SeriesService} from "../../../_services/series.service";
import {NavService} from "../../../_services/nav.service";
import {BookService} from "../../../book-reader/_services/book.service";
import {ThemeService} from "../../../_services/theme.service";
import {AccountService} from "../../../_services/account.service";
import {CHAPTER_ID_DOESNT_EXIST, ReaderService} from "../../../_services/reader.service";
import {UtilityService} from "../../../shared/_services/utility.service";
import {Chapter} from "../../../_models/chapter";
import {User} from "../../../_models/user/user";
import {YomitanService} from "../../../_services/yomitan.service";
import {OcrService} from "../../../_services/ocr.service";
import {OcrPage} from "../../../_models/ocr/ocr";
import {OcrStatus} from "../../../_models/ocr-status";
import {EVENTS, MessageHubService} from "../../../_services/message-hub.service";
import {drawOcrLayer, OCR_LAYER_CLASS, setOcrOutlines} from "../../_helpers/ocr-overlay";
import {OcrFind} from "../../_helpers/ocr-find";

const KEYBIND_TARGETS = [
  {keyBindTarget: KeyBindTarget.OpenHelp},
  {keyBindTarget: KeyBindTarget.Escape},
  {keyBindTarget: KeyBindTarget.FirstPage, description: 'first-page'},
  {keyBindTarget: KeyBindTarget.LastPage, description: 'last-page'},
];

@Component({
  selector: 'app-pdf-reader',
  templateUrl: './pdf-reader.component.html',
  styleUrls: ['./pdf-reader.component.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [NgStyle, NgxExtendedPdfViewerModule, NgbTooltip, TranslocoDirective,
    PdfScrollModeTypePipe, PdfSpreadTypePipe]
})
export class PdfReaderComponent implements OnInit, OnDestroy {

  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly seriesService = inject(SeriesService);
  private readonly navService = inject(NavService);
  private readonly toastr = inject(ToastrService);
  private readonly bookService = inject(BookService);
  private readonly themeService = inject(ThemeService);
  private readonly cdRef = inject(ChangeDetectorRef);
  public readonly accountService = inject(AccountService);
  public readonly readerService = inject(ReaderService);
  public readonly utilityService = inject(UtilityService);
  public readonly destroyRef = inject(DestroyRef);
  public readonly document = inject(DOCUMENT);
  private readonly keyBindService = inject(KeyBindService);
  protected readonly breakpointService = inject(BreakpointService);
  private readonly yomitanService = inject(YomitanService);
  private readonly ocrService = inject(OcrService);
  private readonly messageHub = inject(MessageHubService);
  private readonly pdfNotificationService = inject(PDFNotificationService);


  readonly container = viewChild.required<ElementRef>('container');

  libraryId!: number;
  seriesId!: number;
  volumeId!: number;
  chapterId!: number;
  chapter!: Chapter;
  user!: User;
  readingProfile!: ReadingProfile;

  /**
   * Reading List id. Defaults to -1.
   */
  readingListId: number = CHAPTER_ID_DOESNT_EXIST;

  /**
   * If this is true, no progress will be saved.
   */
  incognitoMode: boolean = false;

  /**
   * If this is true, chapters will be fetched in the order of a reading list, rather than natural series order.
   */
  readingListMode: boolean = false;

  /**
   * Current Page number
   */
  currentPage: number = 1;
  /**
   * Total pages
   */
  maxPages: number = 1;
  bookTitle: string = '';

  zoomSetting: string | number = 'auto';

  theme: 'dark' | 'light' = 'light';
  themeMap: {[key:string]: {background: string, font: string}} = {
    'dark': {'background': '#292929', 'font': '#d9d9d9'},
    'light': {'background': '#f9f9f9', 'font': '#5a5a5a'}
  }
  backgroundColor: string = this.themeMap[this.theme].background;
  fontColor: string = this.themeMap[this.theme].font;

  /**
   * True if Preferences.DataSaver is true
   */
  disableLoadingIndicator = computed(() => this.accountService.userPreferences()?.dataSaver);
  isLoading: boolean = true;
  /**
   * How much of the current document is loaded
   */
  loadPercent: number = 0;
  scrollbarNeeded = false;

  pageLayoutMode: PageViewModeType = 'multiple';
  scrollMode: ScrollModeType = ScrollModeType.vertical;
  spreadMode: SpreadType = 'off';
  isSearchOpen: boolean = false;

  canDownload = computed(() =>
    this.accountService.hasDownloadRole()
  );

  /**
   * Click-to-look-up with Yomitan. Starts from the reading profile, the toolbar button changes it for this session only.
   */
  yomitanEnabled = signal(false);

  /**
   * 1-based pages that have OCR text. Empty for PDFs that carry their own text
   */
  ocrPages = signal<ReadonlySet<number>>(new Set());
  /**
   * Outlines the OCR'd lines, tinted by how sure the OCR was, so a misread kanji can be told from a missing dictionary entry
   */
  ocrOutlines = signal(false);
  private readonly ocrPageCache = new Map<number, OcrPage>();
  /**
   * The latest div pdf.js rendered for each 1-based page. pdf.js replaces a page's contents on every render
   */
  private readonly renderedPageDivs = new Map<number, HTMLElement>();
  /**
   * Lets the find bar search OCR text. It loads every OCR'd page on the first search, since find covers pages pdf.js hasn't rendered
   */
  private readonly ocrFind = new OcrFind(this.document, () => this.loadOcrPagesForFind());

  constructor() {
      this.navService.hideNavBar();
      this.themeService.clearThemes();
      this.navService.hideSideNav();

      this.keyBindService.registerListener(
        this.destroyRef,
        (e) => {
          switch (e.target) {
            case KeyBindTarget.Escape:
              if (this.yomitanService.isPopupVisible()) {
                this.yomitanService.hidePopup();
                return;
              }
              this.closeReader();
              break;
            case KeyBindTarget.FirstPage:
              this.currentPage = 0;
              this.cdRef.markForCheck();
              break;
            case KeyBindTarget.LastPage:
              this.currentPage = this.maxPages;
              this.cdRef.markForCheck();
              break;
            case KeyBindTarget.OpenHelp:
              this.openShortcutModal();
          }

        },
        KEYBIND_TARGETS.map(k => k.keyBindTarget as KeyBindTarget),
      );

      effect(() => {
        const prefs = this.accountService.userPreferences();
        if (prefs) {
          pdfDefaultOptions.disableAutoFetch = prefs.dataSaver;
          this.cdRef.markForCheck();
        }
      });

      effect(() => {
        const app = this.pdfNotificationService.onPDFJSInitSignal();
        if (app) this.ocrFind.install(app);
      });

      effect(() => {
        if (this.yomitanEnabled()) {
          // pdf.js positions every text run absolutely, and so does the OCR layer, so layout-aware scanning would end
          // each word at its span. One :is() keeps Yomitan's "selector, selector *" check matching inside both layers
          const selector = `ngx-extended-pdf-viewer :is(.textLayer, .${OCR_LAYER_CLASS})`;
          this.yomitanService.enableReaderScanning(selector, {layoutAwareScan: false}).catch(err => {
            console.error('Yomitan failed to start', err);
            this.toastr.error(translate('book-reader.yomitan-failed'));
          });
        } else {
          void this.yomitanService.disableReaderScanning();
        }
      });
  }

  @HostListener('window:resize', ['$event'])
  @HostListener('window:orientationchange', ['$event'])
  onResize(event: Event){
    // Update the window Height
    this.calcScrollbarNeeded();
  }

  ngOnDestroy(): void {
    this.themeService.currentTheme$.pipe(take(1)).subscribe(theme => {
      this.themeService.setTheme(theme.name);
    });

    this.navService.showNavBar();
    this.navService.showSideNav();
    this.readerService.disableWakeLock();
    void this.yomitanService.disableReaderScanning();
    this.ocrFind.dispose();

    window.removeEventListener('keydown', this.downloadHandler, { capture: true });
  }

  ngOnInit(): void {
    const libraryId = this.route.snapshot.paramMap.get('libraryId');
    const seriesId = this.route.snapshot.paramMap.get('seriesId');
    const chapterId = this.route.snapshot.paramMap.get('chapterId');

    if (libraryId === null || seriesId === null || chapterId === null) {
      this.router.navigateByUrl('/home');
      return;
    }

    this.libraryId = parseInt(libraryId, 10);
    this.seriesId = parseInt(seriesId, 10);
    this.chapterId = parseInt(chapterId, 10);
    this.incognitoMode = this.route.snapshot.queryParamMap.get('incognitoMode') === 'true';

    this.route.data.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(data => {
      this.readingProfile = data['readingProfile'];
      if (this.readingProfile == null) {
        this.router.navigateByUrl('/home');
        return;
      }
      this.setupReaderSettings();
      this.cdRef.markForCheck();
    });


    const readingListId = this.route.snapshot.queryParamMap.get('readingListId');
    if (readingListId != null) {
      this.readingListMode = true;
      this.readingListId = parseInt(readingListId, 10);
    }

    this.cdRef.markForCheck();

    window.addEventListener('keydown', this.downloadHandler, { capture: true });

    this.init();
  }

  private downloadHandler = (event: KeyboardEvent) => {
    if (event.ctrlKey && event.key.toLowerCase() === 's') {
      if (!this.accountService.hasDownloadRole()) {
        event.preventDefault();
        event.stopImmediatePropagation(); // Stops ALL other handlers
      }
    }
  };


  calcScrollbarNeeded() {
    const viewContainer = this.document.querySelector('#viewerContainer');
    if (viewContainer == null) return;
    this.scrollbarNeeded = viewContainer.scrollHeight > this.container()?.nativeElement?.clientHeight;
    this.cdRef.markForCheck();
  }

  convertPdfLayoutMode(mode: PdfLayoutMode) {
    switch (mode) {
      case PdfLayoutMode.Multiple:
        return 'multiple';
      case PdfLayoutMode.Single:
        return 'single';
      case PdfLayoutMode.Book:
        return 'book';
      case PdfLayoutMode.InfiniteScroll:
        return 'infinite-scroll';

    }
  }

  convertPdfScrollMode(mode: PdfScrollMode) {
    switch (mode) {
      case PdfScrollMode.Vertical:
        return ScrollModeType.vertical;
      case PdfScrollMode.Horizontal:
        return ScrollModeType.horizontal;
      case PdfScrollMode.Wrapped:
        return ScrollModeType.wrapped;
      case PdfScrollMode.Page:
        return ScrollModeType.page;
    }
  }

  convertPdfSpreadMode(mode: PdfSpreadMode): SpreadType {
    switch (mode) {
      case PdfSpreadMode.None:
        return 'off' as SpreadType;
      case PdfSpreadMode.Odd:
        return 'odd' as SpreadType;
      case PdfSpreadMode.Even:
        return 'even' as SpreadType;
    }
  }

  convertPdfTheme(theme: PdfTheme) {
    switch (theme) {
      case PdfTheme.Dark:
        return 'dark';
      case PdfTheme.Light:
        return 'light';
    }
  }

  setupReaderSettings() {
    this.pageLayoutMode = this.convertPdfLayoutMode(PdfLayoutMode.Multiple);
    this.scrollMode = this.convertPdfScrollMode(this.readingProfile.pdfScrollMode || PdfScrollMode.Vertical);
    this.spreadMode = this.convertPdfSpreadMode(this.readingProfile.pdfSpreadMode || PdfSpreadMode.None);
    this.theme = this.convertPdfTheme(this.readingProfile.pdfTheme || PdfTheme.Dark);
    this.yomitanEnabled.set(this.readingProfile.bookReaderYomitanEnabled);
  }

  init() {
    this.backgroundColor = this.themeMap[this.theme].background;
    this.fontColor = this.themeMap[this.theme].font; // TODO: Move this to an observable or something

    this.calcScrollbarNeeded();

    this.bookService.getBookInfo(this.chapterId).subscribe(info => {
      this.volumeId = info.volumeId;
      this.bookTitle = info.bookTitle;
      this.cdRef.markForCheck();
    });

    this.readerService.getProgress(this.chapterId).subscribe(progress => {
      this.currentPage = progress.pageNum || 1;
      this.cdRef.markForCheck();
    });

    this.seriesService.getChapter(this.chapterId).subscribe(chapter => {
      this.maxPages = chapter.pages;

      if (this.currentPage >= this.maxPages) {
        this.currentPage = this.maxPages - 1;
        // Don't save progress on first load to avoid session creation, wait for a page change event
      }
      this.cdRef.markForCheck();
    });
    setTimeout(() => this.readerService.enableWakeLock(this.container().nativeElement), 1000);

    this.initOcr();
  }

  private initOcr() {
    this.ocrService.getChapterStatus(this.chapterId).subscribe(status => {
      if (!status) return;
      if ([OcrStatus.NotChecked, OcrStatus.Pending, OcrStatus.Processing].includes(status.status)) {
        this.ocrService.prioritizeChapter(this.chapterId).subscribe();
      }
      this.addOcrPages(status.pages);
    });

    this.messageHub.messages$.pipe(
      filter(message => message.event === EVENTS.OcrPagesReady),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe(message => {
      const body = message.payload as {chapterId: number, pages: number[]};
      if (body.chapterId !== this.chapterId) return;
      body.pages.forEach(page => this.ocrPageCache.delete(page));
      this.addOcrPages(body.pages);
    });
  }

  private addOcrPages(pages: number[]) {
    if (pages.length === 0) return;
    this.ocrPages.update(existing => new Set([...existing, ...pages]));
    pages.forEach(page => this.drawOcrPage(page));
    this.cdRef.markForCheck();
  }

  onPageRendered(event: PageRenderedEvent) {
    // Rotation is only set by the file itself, since the reader hides the rotate button, and the overlay doesn't handle it yet
    if (event.source.rotation !== 0) return;
    this.renderedPageDivs.set(event.pageNumber, event.source.div);
    this.drawOcrPage(event.pageNumber);
  }

  private drawOcrPage(pageNum: number) {
    const div = this.renderedPageDivs.get(pageNum);
    if (!div || !div.isConnected || !this.ocrPages().has(pageNum)) return;

    const cached = this.ocrPageCache.get(pageNum);
    if (cached) {
      this.ocrFind.paint(drawOcrLayer(div, cached, this.ocrOutlines()));
      return;
    }

    this.ocrService.getPage(this.chapterId, pageNum).subscribe(page => {
      if (!page) return;
      this.ocrPageCache.set(pageNum, page);
      // The page may have been re-rendered while its lines were loading
      const current = this.renderedPageDivs.get(pageNum);
      if (current?.isConnected) this.ocrFind.paint(drawOcrLayer(current, page, this.ocrOutlines()));
    });
  }

  private async loadOcrPagesForFind() {
    const pages = await firstValueFrom(this.ocrService.getChapterPages(this.chapterId)) ?? [];
    // These were fetched just now, so they're at least as new as anything cached
    pages.forEach(page => this.ocrPageCache.set(page.pageNumber, page));
    return pages;
  }

  toggleOcrOutlines() {
    this.ocrOutlines.update(outlined => !outlined);
    setOcrOutlines(this.container().nativeElement, this.ocrOutlines());
  }

  /**
   * Turns off Incognito mode. This can only happen once if the user clicks the icon. This will modify URL state
   */
   turnOffIncognito() {
    this.incognitoMode = false;
    const newRoute = this.readerService.getNextChapterUrl(this.router.url, this.chapterId, this.incognitoMode, this.readingListMode, this.readingListId);
    window.history.replaceState({}, '', newRoute);
    this.toastr.info(translate('toasts.incognito-off'));
    this.saveProgress();
    this.cdRef.markForCheck();
  }

  toggleTheme() {
    if (this.theme === 'dark') {
      this.theme = 'light';
    } else {
      this.theme = 'dark';
    }
    this.backgroundColor = this.themeMap[this.theme].background;
    this.fontColor = this.themeMap[this.theme].font;
    this.cdRef.markForCheck();
  }

  toggleScrollMode() {
    const options: Array<ScrollModeType> = [ScrollModeType.vertical, ScrollModeType.horizontal, ScrollModeType.page];
    let index = options.indexOf(this.scrollMode) + 1;
    if (index >= options.length) index = 0;
    this.scrollMode = options[index];

    this.calcScrollbarNeeded();
    const currPage = this.currentPage;
    this.cdRef.markForCheck();

    setTimeout(() => {
      this.currentPage = currPage;
      this.cdRef.markForCheck();
    }, 100);
  }

  toggleYomitan() {
    this.yomitanEnabled.update(enabled => !enabled);
  }

  toggleSpreadMode() {
     const options: Array<SpreadType> = ['off', 'odd', 'even'];
     let index = options.indexOf(this.spreadMode) + 1;
     if (index >= options.length) index = 0;
     this.spreadMode = options[index];


    this.cdRef.markForCheck();
  }

  toggleBookPageMode() {
    if (this.pageLayoutMode === 'book') {
      this.pageLayoutMode = 'multiple';
    } else {
      if (this.breakpointService.activeBreakpoint() < Breakpoint.Tablet) {
        this.toastr.info(translate('toasts.pdf-book-mode-screen-size'));
        return;
      }
      this.pageLayoutMode = 'book';
      // If the fit is automatic, let's adjust to 100% to ensure it renders correctly (can't do this, but it doesn't always happen)
    }
    this.cdRef.markForCheck();
  }

  saveProgress() {
    if (this.incognitoMode) return;
    this.readerService.saveProgress(this.libraryId, this.seriesId, this.volumeId, this.chapterId, this.currentPage).subscribe();
  }

  closeReader() {
     if (this.readerService.closeShortCutModal()) return;
     this.readerService.closeReader(this.libraryId, this.seriesId, this.chapterId, this.readingListMode, this.readingListId);
  }

  openShortcutModal() {
     this.readerService.openShortcutModal(KEYBIND_TARGETS);
  }

  updateLoading(state: boolean) {
    this.isLoading = state;
    this.cdRef.markForCheck();
  }

  updateLoadProgress(event: ProgressBarEvent) {
    this.loadPercent = event.percent;
    this.cdRef.markForCheck();
  }


  updateSearchOpen(event: boolean) {
     this.isSearchOpen = event;
     this.cdRef.markForCheck();
  }

  prevPage() {
     this.currentPage--;
     if (this.currentPage < 0) this.currentPage = 0;
     this.cdRef.markForCheck();
  }

  nextPage() {
    this.currentPage++;
    if (this.currentPage > this.maxPages) this.currentPage = this.maxPages;
    this.cdRef.markForCheck();
  }


  protected readonly Breakpoint = Breakpoint;
  protected readonly ScrollModeType = ScrollModeType;
}
