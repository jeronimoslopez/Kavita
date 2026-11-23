import {ComponentRef, DestroyRef, DOCUMENT, inject, Injectable, Injector} from '@angular/core';
import {
  createGlobalPositionStrategy,
  createNoopScrollStrategy,
  createOverlayRef,
  GlobalPositionStrategy,
  OverlayRef,
} from '@angular/cdk/overlay';
import {ComponentPortal} from '@angular/cdk/portal';
import {Subscription} from 'rxjs';
import {DictionaryPopupComponent} from './dictionary-popup.component';
import {isOutOfView, placePopover, POPOVER_MIN_WIDTH, Size} from './popup-position';
import {DictionaryPopupMode, DictionaryPopupView} from './popup-state';

export interface PopupShowOptions {
  vertical: boolean;
  viaKeyboard: boolean;
}

export interface PopupHandlers {
  part(sub: number): void;
  back(): void;
  /** The popup closed itself, on Close, Escape, an outside pointer-down or the word scrolling away */
  closed(): void;
}

/**
 * Puts DictionaryPopupComponent on screen in a CDK overlay. From 768px it's a popover beside the tapped word, under
 * that a sheet at the bottom. Neither has a backdrop or a focus trap, so the page behind stays readable and tappable.
 * Provide it next to ReaderDictionaryController
 */
@Injectable()
export class DictionaryPopupPresenter {

  private readonly injector = inject(Injector);
  private readonly document = inject(DOCUMENT);

  private overlayRef: OverlayRef | null = null;
  private strategy: GlobalPositionStrategy | null = null;
  private componentRef: ComponentRef<DictionaryPopupComponent> | null = null;
  private outputs = new Subscription();
  private resizeObserver: ResizeObserver | null = null;
  private anchor: Range | null = null;
  private vertical = false;
  private mode: DictionaryPopupMode = 'popover';
  private handlers: PopupHandlers | null = null;
  private returnFocus: HTMLElement | null = null;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.hide());
  }

  isVisible(): boolean {
    return this.overlayRef !== null;
  }

  /** Opens the popup on a word, or moves it there when it's already open */
  show(anchor: Range, options: PopupShowOptions, handlers: PopupHandlers): void {
    this.anchor = anchor;
    this.vertical = options.vertical;
    this.handlers = handlers;
    const active = this.document.activeElement;
    if (this.overlayRef === null) {
      this.returnFocus = active instanceof HTMLElement && active !== this.document.body ? active : null;
      this.create();
    }
    this.componentRef?.setInput('viaKeyboard', options.viaKeyboard);
    this.reposition();
  }

  render(view: DictionaryPopupView): void {
    this.componentRef?.setInput('view', view);
  }

  /** Closes the popup without telling the handlers, for when the caller closes it */
  hide(): void {
    if (this.overlayRef === null) return;
    this.removeListeners();
    this.outputs.unsubscribe();
    this.outputs = new Subscription();
    this.resizeObserver?.disconnect();
    this.resizeObserver = null;
    this.overlayRef.dispose();
    this.overlayRef = null;
    this.strategy = null;
    this.componentRef = null;
    this.anchor = null;
    this.handlers = null;
  }

  private create() {
    this.strategy = createGlobalPositionStrategy(this.injector);
    this.overlayRef = createOverlayRef(this.injector, {
      positionStrategy: this.strategy,
      scrollStrategy: createNoopScrollStrategy(),
      hasBackdrop: false,
      // Kept in the overlay container under body, where the reader theme's variables reach it
      usePopover: false,
      disposeOnNavigation: true,
    });
    this.overlayRef.overlayElement.style.visibility = 'hidden';

    const ref = this.overlayRef.attach(new ComponentPortal(DictionaryPopupComponent, null, this.injector));
    this.componentRef = ref;
    this.mode = this.currentMode();
    ref.setInput('mode', this.mode);
    this.outputs.add(ref.instance.part.subscribe(sub => this.handlers?.part(sub)));
    this.outputs.add(ref.instance.back.subscribe(() => this.handlers?.back()));
    this.outputs.add(ref.instance.dismiss.subscribe(() => this.closeSelf(true)));

    if (typeof ResizeObserver === 'function') {
      // The popup grows when a lookup answers, and a popover above the word has to move up with it
      this.resizeObserver = new ResizeObserver(() => this.reposition());
      this.resizeObserver.observe(this.overlayRef.overlayElement);
    }
    this.addListeners();
  }

  private currentMode(): DictionaryPopupMode {
    const view = this.document.defaultView;
    return view?.matchMedia?.(`(min-width: ${POPOVER_MIN_WIDTH}px)`).matches === false ? 'sheet' : 'popover';
  }

  private viewport(): Size {
    const root = this.document.documentElement;
    return {width: root.clientWidth, height: root.clientHeight};
  }

  private reposition = () => {
    const overlayRef = this.overlayRef;
    const strategy = this.strategy;
    if (overlayRef === null || strategy === null || this.anchor === null) return;

    const mode = this.currentMode();
    if (mode !== this.mode) {
      this.mode = mode;
      this.componentRef?.setInput('mode', mode);
    }

    if (mode === 'sheet') {
      overlayRef.updateSize({width: '100%'});
      strategy.left('0px').bottom('0px');
    } else {
      overlayRef.updateSize({width: ''});
      const pane = overlayRef.overlayElement.getBoundingClientRect();
      const word = this.anchor.getBoundingClientRect();
      const place = placePopover(word, this.viewport(), {width: pane.width, height: pane.height}, this.vertical);
      strategy.left(`${place.left}px`).top(`${place.top}px`);
    }
    overlayRef.updatePosition();
    overlayRef.overlayElement.style.visibility = '';
  };

  private onPointerDown = (event: PointerEvent) => {
    const target = event.target;
    if (target instanceof Node && this.overlayRef?.overlayElement.contains(target)) return;
    this.closeSelf(false);
  };

  private onKeyDown = (event: KeyboardEvent) => {
    if (event.key !== 'Escape') return;
    // The reader closes itself on Escape, so the key stops here while the popup is open
    event.preventDefault();
    event.stopPropagation();
    this.closeSelf(true);
  };

  private onScroll = (event: Event) => {
    const target = event.target;
    if (target instanceof Node && this.overlayRef?.overlayElement.contains(target)) return;
    if (this.anchor === null) return;
    if (isOutOfView(this.anchor.getBoundingClientRect(), this.viewport())) {
      this.closeSelf(false);
      return;
    }
    this.reposition();
  };

  private addListeners() {
    const view = this.document.defaultView;
    this.document.addEventListener('pointerdown', this.onPointerDown, true);
    view?.addEventListener('keydown', this.onKeyDown, true);
    // Capture, so scrolling inside the reader's own scroll container counts too
    this.document.addEventListener('scroll', this.onScroll, {capture: true, passive: true});
    view?.addEventListener('resize', this.onScroll);
  }

  private removeListeners() {
    const view = this.document.defaultView;
    this.document.removeEventListener('pointerdown', this.onPointerDown, true);
    view?.removeEventListener('keydown', this.onKeyDown, true);
    this.document.removeEventListener('scroll', this.onScroll, {capture: true});
    view?.removeEventListener('resize', this.onScroll);
  }

  private closeSelf(restoreFocus: boolean) {
    const handlers = this.handlers;
    const focus = this.returnFocus;
    this.hide();
    this.returnFocus = null;
    if (restoreFocus && focus?.isConnected) focus.focus({preventScroll: true});
    handlers?.closed();
  }
}
