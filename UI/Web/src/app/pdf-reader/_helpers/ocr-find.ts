import {OcrPage} from '../../_models/ocr/ocr';
import {drawnLines, OCR_LAYER_CLASS, OcrLineMatch, paintOcrMatches} from '../../reader-shared/_helpers/ocr-overlay';

/**
 * Makes pdf.js's find search OCR text, so OCR matches share its counter, its next and previous, and its options.
 *
 * pdf.js doesn't export its find controller class, so this patches the instance ngx-extended-pdf-viewer creates. It
 * relies on parts of pdf.js 6.2 (ngx-extended-pdf-viewer 30) that aren't public, so check them when the viewer is
 * upgraded. The controller reads page text only through its document's getPage().getTextContent(). It keeps matches in
 * _pageMatches and _pageMatchesLength as offsets into the page's items joined with nothing between them. And pdf.js's
 * TextHighlighter reads them only through the public pageMatches and pageMatchesLength getters.
 */
export class OcrFind {
  /** 0-based indexes of the pages whose find text came from OCR. pdf.js's own highlighter is kept away from them */
  private readonly ocrPageIndexes = new Set<number>();
  /** The OCR pages find searches, by 1-based page number. Fetched on the first search */
  private pages: Promise<Map<number, OcrPage>> | null = null;
  private loadedPages = new Map<number, OcrPage>();
  private findController: any = null;
  private eventBus: any = null;
  private originalSetDocument: ((pdfDocument: unknown) => void) | null = null;
  private readonly wrappedDocuments = new WeakMap<object, object>();
  private readonly onUpdateMatches = ({pageIndex}: {pageIndex: number}) => this.repaint(pageIndex);

  constructor(private readonly root: ParentNode, private readonly loadPages: () => Promise<OcrPage[]>) {}

  /**
   * Patches the viewer's find controller. Call it when ngx-extended-pdf-viewer hands over PDFViewerApplication, which is
   * before it opens the document.
   *
   * @returns False when the controller isn't shaped the way this expects, and find stays as pdf.js ships it
   */
  install(app: any): boolean {
    const findController = app?.findController;
    const eventBus = app?.eventBus;
    if (typeof findController?.setDocument !== 'function' || !Array.isArray(findController._pageMatches)
      || typeof eventBus?.on !== 'function') {
      console.warn('Find over OCR text is off: the pdf.js find controller has changed');
      return false;
    }
    if (findController === this.findController) return true;
    this.dispose();

    this.findController = findController;
    this.eventBus = eventBus;
    this.originalSetDocument = findController.setDocument;
    findController.setDocument = (pdfDocument: any) => {
      this.ocrPageIndexes.clear();
      this.pages = null;
      this.loadedPages = new Map();
      this.originalSetDocument!.call(findController, pdfDocument ? this.wrapDocument(pdfDocument) : pdfDocument);
    };
    if (findController._pdfDocument) findController._pdfDocument = this.wrapDocument(findController._pdfDocument);

    const hideOcrPages = (all: unknown[]) => new Proxy(all, {
      get: (target, key) => typeof key === 'string' && this.ocrPageIndexes.has(Number(key)) ? undefined : Reflect.get(target, key),
    });
    Object.defineProperty(findController, 'pageMatches', {configurable: true, get: () => hideOcrPages(findController._pageMatches)});
    Object.defineProperty(findController, 'pageMatchesLength', {configurable: true, get: () => hideOcrPages(findController._pageMatchesLength)});

    eventBus.on('updatetextlayermatches', this.onUpdateMatches);
    return true;
  }

  /** Puts the find controller back the way pdf.js made it. ngx-extended-pdf-viewer reuses it for the next PDF */
  dispose() {
    const findController = this.findController;
    if (!findController) return;

    findController.setDocument = this.originalSetDocument;
    delete findController.pageMatches;
    delete findController.pageMatchesLength;
    const original = this.originalOf(findController._pdfDocument);
    if (original !== findController._pdfDocument) findController._pdfDocument = original;
    this.eventBus?.off('updatetextlayermatches', this.onUpdateMatches);

    this.findController = null;
    this.eventBus = null;
    this.originalSetDocument = null;
  }

  /**
   * Paints find's matches on a drawn layer. pdf.js empties a page every time it renders it, so the reader calls this
   * after each draw, and find's own updates call it for layers already on the page.
   */
  paint(layer: HTMLElement) {
    const findController = this.findController;
    const pageNumber = Number(layer.dataset['pageNumber']);
    const page = this.loadedPages.get(pageNumber);
    if (!findController || !this.ocrPageIndexes.has(pageNumber - 1)) return;

    // A page OCR'd again after the first search is drawn from lines find never searched
    const matches = page && `${page.version}` === layer.dataset['version'] ? this.lineMatches(page, pageNumber - 1) : [];
    const selected = paintOcrMatches(layer, matches);
    if (selected) {
      findController.scrollMatchIntoView({element: selected, pageIndex: pageNumber - 1, matchIndex: findController.selected.matchIdx});
    }
  }

  private repaint(pageIndex: number) {
    const selector = pageIndex === -1 ? `.${OCR_LAYER_CLASS}` : `.${OCR_LAYER_CLASS}[data-page-number="${pageIndex + 1}"]`;
    this.root.querySelectorAll<HTMLElement>(selector).forEach(layer => this.paint(layer));
  }

  /** Splits the page's matches into the parts that fall on each drawn line, keeping the ones pdf.js would show */
  private lineMatches(page: OcrPage, pageIndex: number): OcrLineMatch[] {
    const findController = this.findController;
    const starts: number[] | undefined = findController._pageMatches[pageIndex];
    const lengths: number[] | undefined = findController._pageMatchesLength[pageIndex];
    if (!findController.highlightMatches || !starts || !lengths) return [];

    const highlightAll = !!findController.state?.highlightAll;
    const {pageIdx, matchIdx} = findController.selected;
    const lines = drawnLines(page);
    // pdf.js searches with a newline after each line but maps matches back to the lines' text alone, the way its own
    // highlighter counts text items, so each line starts where the one before it ends
    const lineStarts: number[] = [];
    let at = 0;
    for (const line of lines) {
      lineStarts.push(at);
      at += line.text.length;
    }

    const result: OcrLineMatch[] = [];
    starts.forEach((start, m) => {
      const selected = pageIdx === pageIndex && matchIdx === m;
      if (!selected && !highlightAll) return;

      const end = start + lengths[m];
      lines.forEach((line, k) => {
        const from = Math.max(start, lineStarts[k]) - lineStarts[k];
        const to = Math.min(end, lineStarts[k] + line.text.length) - lineStarts[k];
        if (from < to) result.push({line: k, start: from, end: to, selected});
      });
    });
    return result;
  }

  /**
   * Hands find a document whose pages with OCR text read as their OCR lines, one item per drawn line. Those pages
   * never show find their own text, so a PDF whose text was replaced by OCR doesn't match each word twice.
   */
  private wrapDocument(pdfDocument: any) {
    const getPage = async (pageNumber: number) => {
      const ocrPage = (await this.loadOnce()).get(pageNumber);
      if (!ocrPage) return pdfDocument.getPage(pageNumber);

      this.ocrPageIndexes.add(pageNumber - 1);
      const items = drawnLines(ocrPage).map(line => ({str: line.text, hasEOL: true}));
      return {getTextContent: async () => ({items, styles: {}})};
    };

    const wrapped = new Proxy(pdfDocument, {
      get: (target, key) => {
        if (key === 'getPage') return getPage;
        const value = Reflect.get(target, key);
        return typeof value === 'function' ? value.bind(target) : value;
      },
    });
    this.wrappedDocuments.set(wrapped, pdfDocument);
    return wrapped;
  }

  private originalOf(pdfDocument: any) {
    return (pdfDocument && this.wrappedDocuments.get(pdfDocument)) ?? pdfDocument;
  }

  private loadOnce() {
    this.pages ??= this.loadPages()
      .catch(err => {
        console.error('Could not load OCR text for find', err);
        return [] as OcrPage[];
      })
      .then(pages => {
        this.loadedPages = new Map(pages.map(page => [page.pageNumber, page]));
        return this.loadedPages;
      });
    return this.pages;
  }
}
