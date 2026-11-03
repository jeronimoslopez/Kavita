import {OcrPage} from '../../_models/ocr/ocr';
import {drawOcrLayer, OCR_LAYER_CLASS} from '../../reader-shared/_helpers/ocr-overlay';

/** Something on screen that shows a page, and which part of the page it shows */
export interface OcrTarget {
  element: HTMLElement;
  /** The page's image URL, which carries its chapter and page number */
  src: string;
  /** Set when the element shows one half of a wide page, as the canvas renderer does when it splits one */
  half?: 'left' | 'right';
}

/**
 * Keeps an OCR layer over each page the reader shows.
 *
 * The layers live in one host element instead of inside the renderers, each in a box placed over its page in viewport
 * coordinates. Observers on the reading area and the document move the boxes whenever a page loads, resizes, changes
 * or scrolls, so fit modes, double pages and zoom need nothing of their own. A half page gets a layer twice its box's
 * width, shifted so the half that shows lines up, and clipped to the box.
 */
export class MangaOcrOverlay {
  private readonly slots = new Map<HTMLElement, HTMLElement>();
  /** The page and URL each slot's layer was drawn for */
  private readonly drawn = new Map<HTMLElement, {page: OcrPage, src: string}>();
  private readonly resizeObserver = new ResizeObserver(() => this.schedule());
  private readonly mutationObserver: MutationObserver;
  private readonly onChange = () => this.schedule();
  private readonly onWheel = (event: WheelEvent) => this.forwardWheel(event);
  private readonly onTouchStart = (event: TouchEvent) => this.startTouch(event);
  private readonly onTouchMove = (event: TouchEvent) => this.forwardTouch(event);
  private touch: {x: number, y: number, scroller: HTMLElement | null} | null = null;
  private frame = 0;
  private outlined = false;

  /**
   * @param root The reading area, watched for anything that moves a page
   * @param host Where the layer boxes go. It has to cover the viewport and not clip or transform its children
   * @param targets The pages on screen right now
   * @param lookup The lines for a page URL, or null while there are none or they're loading
   * @param paint Called with each layer after it's drawn and on {@link repaint}, to mark find's matches
   */
  constructor(private readonly root: HTMLElement, private readonly host: HTMLElement,
              private readonly targets: () => OcrTarget[],
              private readonly lookup: (src: string) => OcrPage | null,
              private readonly paint: (layer: HTMLElement, page: OcrPage, src: string) => void = () => {}) {
    // Moving the boxes changes the host's subtree, which mustn't schedule another pass
    this.mutationObserver = new MutationObserver(records => {
      if (records.some(record => !host.contains(record.target))) this.schedule();
    });
    this.mutationObserver.observe(root, {subtree: true, childList: true, attributes: true,
      attributeFilter: ['src', 'class', 'style', 'width', 'height']});
    // Scroll and image load events don't bubble, so they're caught on the way down. Webtoon mode scrolls the document
    document.addEventListener('scroll', this.onChange, {capture: true, passive: true});
    root.addEventListener('load', this.onChange, true);
    window.addEventListener('resize', this.onChange);
    // The boxes are fixed, so the browser hands a scroll that starts on their text to the root scroller, which the
    // reader doesn't use. These pass it on to whatever scrolls under the text
    host.addEventListener('wheel', this.onWheel, {passive: false});
    host.addEventListener('touchstart', this.onTouchStart, {passive: true});
    host.addEventListener('touchmove', this.onTouchMove, {passive: false});
    this.schedule();
  }

  /** Draws again after lines arrive or change, or after a split page turns to its other half */
  refresh() {
    this.slots.forEach(slot => delete slot.dataset['key']);
    this.schedule();
  }

  /** Calls paint again on every layer on screen, after find's matches change */
  repaint() {
    this.drawn.forEach(({page, src}, slot) => {
      const layer = slot.querySelector<HTMLElement>(`.${OCR_LAYER_CLASS}`);
      if (layer) this.paint(layer, page, src);
    });
  }

  setOutlined(outlined: boolean) {
    this.outlined = outlined;
    this.host.querySelectorAll(`.${OCR_LAYER_CLASS}`).forEach(layer => layer.classList.toggle('outlined', outlined));
  }

  dispose() {
    cancelAnimationFrame(this.frame);
    this.mutationObserver.disconnect();
    this.resizeObserver.disconnect();
    document.removeEventListener('scroll', this.onChange, {capture: true});
    this.root.removeEventListener('load', this.onChange, true);
    window.removeEventListener('resize', this.onChange);
    this.host.removeEventListener('wheel', this.onWheel);
    this.host.removeEventListener('touchstart', this.onTouchStart);
    this.host.removeEventListener('touchmove', this.onTouchMove);
    this.slots.forEach(slot => slot.remove());
    this.slots.clear();
    this.drawn.clear();
  }

  private schedule() {
    if (this.frame) return;
    this.frame = requestAnimationFrame(() => {
      this.frame = 0;
      this.sync();
    });
  }

  private sync() {
    const shown = new Set<HTMLElement>();
    for (const {element, src, half} of this.targets()) {
      const rect = element.getBoundingClientRect();
      // Webtoon mode keeps pages loaded above and below the screen, and those wait until they scroll in
      if (rect.width === 0 || rect.height === 0 || rect.bottom < 0 || rect.top > window.innerHeight) continue;
      const page = this.lookup(src);
      if (!page) continue;

      shown.add(element);
      let slot = this.slots.get(element);
      if (!slot) {
        slot = document.createElement('div');
        slot.className = 'kavita-ocr-slot';
        this.host.appendChild(slot);
        this.slots.set(element, slot);
        this.resizeObserver.observe(element);
      }
      Object.assign(slot.style, {
        left: `${rect.left}px`,
        top: `${rect.top}px`,
        width: `${rect.width}px`,
        height: `${rect.height}px`,
      });

      // Lines are stretched to their boxes when drawn, and the stretch holds at any size, so a layer is only drawn
      // again when the box shows another page or half, or the page's lines change
      const key = `${src}|${half ?? 'whole'}|${page.version}`;
      if (slot.dataset['key'] === key) continue;

      // Drawing measures each line against the layer, so the layer takes its full-page size first
      slot.classList.toggle('half', !!half);
      slot.classList.toggle('right-half', half === 'right');
      this.paint(drawOcrLayer(slot, page, this.outlined), page, src);
      this.drawn.set(slot, {page, src});
      slot.dataset['key'] = key;
    }

    for (const [element, slot] of this.slots) {
      if (shown.has(element)) continue;
      slot.remove();
      this.slots.delete(element);
      this.drawn.delete(slot);
      this.resizeObserver.unobserve(element);
    }
  }

  private forwardWheel(event: WheelEvent) {
    const scroller = this.scrollerBelow(event.clientX, event.clientY);
    if (!scroller) return;
    // Lines and pages are rare outside Firefox, and a line of text is about 16 px
    const unit = event.deltaMode === WheelEvent.DOM_DELTA_PAGE ? scroller.clientHeight
      : event.deltaMode === WheelEvent.DOM_DELTA_LINE ? 16 : 1;
    scroller.scrollBy({left: event.deltaX * unit, top: event.deltaY * unit});
    event.preventDefault();
  }

  private startTouch(event: TouchEvent) {
    const t = event.touches[0];
    this.touch = event.touches.length === 1 ? {x: t.clientX, y: t.clientY, scroller: this.scrollerBelow(t.clientX, t.clientY)} : null;
  }

  /** One-finger drags only, so pinch zoom stays the browser's */
  private forwardTouch(event: TouchEvent) {
    if (!this.touch?.scroller || event.touches.length !== 1) return;
    const t = event.touches[0];
    this.touch.scroller.scrollBy({left: this.touch.x - t.clientX, top: this.touch.y - t.clientY});
    this.touch = {...this.touch, x: t.clientX, y: t.clientY};
    event.preventDefault();
  }

  /** The nearest element under a point, outside the host, that can scroll */
  private scrollerBelow(x: number, y: number): HTMLElement | null {
    let element = document.elementsFromPoint(x, y).find(el => !this.host.contains(el)) as HTMLElement | undefined;
    for (; element; element = element.parentElement ?? undefined) {
      const style = getComputedStyle(element);
      const canScrollY = element.scrollHeight > element.clientHeight && /auto|scroll/.test(style.overflowY);
      const canScrollX = element.scrollWidth > element.clientWidth && /auto|scroll/.test(style.overflowX);
      if (canScrollY || canScrollX) return element;
    }
    return null;
  }
}
