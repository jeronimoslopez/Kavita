import {OcrPage} from '../../_models/ocr/ocr';
import {drawOcrLayer, OCR_LAYER_CLASS} from '../../reader-shared/_helpers/ocr-overlay';

/** The page images of the single and double renderers. The canvas renderer and webtoon mode don't get a layer yet */
const PAGE_IMAGES = ['app-single-renderer', 'app-double-renderer', 'app-double-reverse-renderer', 'app-double-no-cover-renderer']
  .map(renderer => `${renderer} img`)
  .join(', ');

/**
 * Keeps an OCR layer over each page image the single and double renderers show.
 *
 * The layers live in one host element instead of inside the renderers, each in a box placed over its image in
 * viewport coordinates. Observers on the reading area move the boxes whenever an image loads, resizes, changes or
 * scrolls, so fit modes, double pages and zoom need nothing of their own.
 */
export class MangaOcrOverlay {
  private readonly slots = new Map<HTMLImageElement, HTMLElement>();
  private readonly resizeObserver = new ResizeObserver(() => this.schedule());
  private readonly mutationObserver: MutationObserver;
  private readonly onChange = () => this.schedule();
  private frame = 0;
  private outlined = false;

  /**
   * @param root The reading area, whose images get layers
   * @param host Where the layer boxes go. It has to cover the viewport and not clip or transform its children
   * @param lookup The lines for an image, or null while there are none or they're loading
   */
  constructor(private readonly root: HTMLElement, private readonly host: HTMLElement,
              private readonly lookup: (img: HTMLImageElement) => OcrPage | null) {
    // Moving the boxes changes the host's subtree, which mustn't schedule another pass
    this.mutationObserver = new MutationObserver(records => {
      if (records.some(record => !host.contains(record.target))) this.schedule();
    });
    this.mutationObserver.observe(root, {subtree: true, childList: true, attributes: true,
      attributeFilter: ['src', 'class', 'style', 'width', 'height']});
    root.addEventListener('scroll', this.onChange, {passive: true});
    // Image load events don't bubble, so they're caught on the way down
    root.addEventListener('load', this.onChange, true);
    window.addEventListener('resize', this.onChange);
    this.schedule();
  }

  /** Draws again after lines arrive or change */
  refresh() {
    this.slots.forEach(slot => delete slot.dataset['key']);
    this.schedule();
  }

  setOutlined(outlined: boolean) {
    this.outlined = outlined;
    this.host.querySelectorAll(`.${OCR_LAYER_CLASS}`).forEach(layer => layer.classList.toggle('outlined', outlined));
  }

  dispose() {
    cancelAnimationFrame(this.frame);
    this.mutationObserver.disconnect();
    this.resizeObserver.disconnect();
    this.root.removeEventListener('scroll', this.onChange);
    this.root.removeEventListener('load', this.onChange, true);
    window.removeEventListener('resize', this.onChange);
    this.slots.forEach(slot => slot.remove());
    this.slots.clear();
  }

  private schedule() {
    if (this.frame) return;
    this.frame = requestAnimationFrame(() => {
      this.frame = 0;
      this.sync();
    });
  }

  private sync() {
    const shown = new Set<HTMLImageElement>();
    for (const img of Array.from(this.root.querySelectorAll<HTMLImageElement>(PAGE_IMAGES))) {
      const rect = img.getBoundingClientRect();
      if (!img.complete || img.naturalWidth === 0 || rect.width === 0 || rect.height === 0) continue;
      const page = this.lookup(img);
      if (!page) continue;

      shown.add(img);
      let slot = this.slots.get(img);
      if (!slot) {
        slot = document.createElement('div');
        slot.className = 'kavita-ocr-slot';
        this.host.appendChild(slot);
        this.slots.set(img, slot);
        this.resizeObserver.observe(img);
      }
      Object.assign(slot.style, {
        left: `${rect.left}px`,
        top: `${rect.top}px`,
        width: `${rect.width}px`,
        height: `${rect.height}px`,
      });

      // Lines are stretched to their boxes when drawn, and the stretch holds at any size, so a layer is only drawn
      // again when the image shows another page or the page's lines change
      const key = `${img.src}|${page.version}`;
      if (slot.dataset['key'] !== key) {
        drawOcrLayer(slot, page, this.outlined);
        slot.dataset['key'] = key;
      }
    }

    for (const [img, slot] of this.slots) {
      if (shown.has(img)) continue;
      slot.remove();
      this.slots.delete(img);
      this.resizeObserver.unobserve(img);
    }
  }
}
