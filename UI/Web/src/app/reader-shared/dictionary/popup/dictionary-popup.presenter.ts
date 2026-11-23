import {ComponentRef, DestroyRef, DOCUMENT, inject, Injectable, Injector} from '@angular/core';
import {Location} from '@angular/common';
import {
  createGlobalPositionStrategy,
  createNoopScrollStrategy,
  createOverlayRef,
  GlobalPositionStrategy,
  OverlayRef,
} from '@angular/cdk/overlay';
import {ComponentPortal} from '@angular/cdk/portal';
import {Subscription, SubscriptionLike} from 'rxjs';
import {DictionaryPopupComponent} from './dictionary-popup.component';
import {
  isOutOfView,
  placePopover,
  POPOVER_HEIGHT_SHARE,
  POPOVER_MIN_WIDTH,
  POPOVER_WIDTH,
  POPOVER_WIDTH_SHARE,
  Size,
} from './popup-position';
import {DictionaryPopupMode, DictionaryPopupView} from './popup-state';

export interface PopupShowOptions {
  vertical: boolean;
  viaKeyboard: boolean;
}

/** Why the popup closed itself */
export type PopupCloseReason = 'close-button' | 'escape' | 'outside' | 'out-of-view' | 'navigation';

export interface PopupHandlers {
  part(sub: number): void;
  back(): void;
  closed(reason: PopupCloseReason): void;
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
  private readonly location = inject(Location, {optional: true});

  private overlayRef: OverlayRef | null = null;
  private strategy: GlobalPositionStrategy | null = null;
  private componentRef: ComponentRef<DictionaryPopupComponent> | null = null;
  private subscriptions = new Subscription();
  private locationSubscription: SubscriptionLike | null = null;
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

  /**
   * Closes the popup without telling the handlers, for when the caller closes it. Focus goes back to where it was
   * before the popup opened only when it's inside the popup, so it isn't left on an element that's gone
   */
  hide(): void {
    const active = this.document.activeElement;
    this.teardown(active !== null && this.overlayRef?.overlayElement.contains(active) === true);
  }

  private teardown(restoreFocus: boolean) {
    const focus = this.returnFocus;
    this.returnFocus = null;
    if (this.overlayRef === null) return;
    this.removeListeners();
    // Unsubscribed first, so the detachment the dispose below causes doesn't read as an outside close
    this.subscriptions.unsubscribe();
    this.subscriptions = new Subscription();
    this.locationSubscription?.unsubscribe();
    this.locationSubscription = null;
    this.resizeObserver?.disconnect();
    this.resizeObserver = null;
    this.overlayRef.dispose();
    this.overlayRef = null;
    this.strategy = null;
    this.componentRef = null;
    this.anchor = null;
    this.handlers = null;
    if (restoreFocus && focus?.isConnected) focus.focus({preventScroll: true});
  }

  private create() {
    this.strategy = createGlobalPositionStrategy(this.injector);
    this.overlayRef = createOverlayRef(this.injector, {
      positionStrategy: this.strategy,
      scrollStrategy: createNoopScrollStrategy(),
      hasBackdrop: false,
      // Kept in the overlay container under body, where the reader theme's variables reach it
      usePopover: false,
      // CDK's own disposal would skip the handlers and leave the open word marked. The location listener below closes
      // through closeSelf instead
      disposeOnNavigation: false,
    });
    this.overlayRef.overlayElement.style.visibility = 'hidden';

    const ref = this.overlayRef.attach(new ComponentPortal(DictionaryPopupComponent, null, this.injector));
    this.componentRef = ref;
    this.mode = this.currentMode();
    ref.setInput('mode', this.mode);
    this.subscriptions.add(ref.instance.part.subscribe(sub => this.handlers?.part(sub)));
    this.subscriptions.add(ref.instance.back.subscribe(() => this.handlers?.back()));
    this.subscriptions.add(ref.instance.dismiss.subscribe(() => this.closeSelf('close-button', true)));
    // Anything else that disposes the overlay still runs the full close
    this.subscriptions.add(this.overlayRef.detachments().subscribe(() => this.closeSelf('navigation', false)));
    this.locationSubscription = this.location?.subscribe(() => this.closeSelf('navigation', false)) ?? null;

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

  /** The popover's size before any cap, so a capped popover doesn't measure itself as fitting next time */
  private naturalSize(viewport: Size): Size {
    const panel = this.overlayRef?.overlayElement.querySelector('.dp');
    const height = panel?.scrollHeight ?? 0;
    return {
      width: Math.min(POPOVER_WIDTH, viewport.width * POPOVER_WIDTH_SHARE),
      height: Math.min(height, viewport.height * POPOVER_HEIGHT_SHARE),
    };
  }

  private reposition = () => {
    const overlayRef = this.overlayRef;
    const strategy = this.strategy;
    if (overlayRef === null || strategy === null || this.anchor === null) return;

    const viewport = this.viewport();
    const word = this.anchor.getBoundingClientRect();
    // Also catches a page that re-rendered under the popup, which collapses the range to an empty box
    if (isOutOfView(word, viewport)) {
      this.closeSelf('out-of-view', false);
      return;
    }

    const mode = this.currentMode();
    if (mode !== this.mode) {
      this.mode = mode;
      this.componentRef?.setInput('mode', mode);
    }

    const pane = overlayRef.overlayElement;
    if (mode === 'sheet') {
      overlayRef.updateSize({width: '100%'});
      pane.style.removeProperty('--dp-max-height');
      pane.style.removeProperty('--dp-max-width');
      strategy.left('0px').bottom('0px');
    } else {
      overlayRef.updateSize({width: ''});
      const place = placePopover(word, viewport, this.naturalSize(viewport), this.vertical);
      setCap(pane, '--dp-max-height', place.maxHeight);
      setCap(pane, '--dp-max-width', place.maxWidth);
      strategy.left(`${place.left}px`).top(`${place.top}px`);
    }
    overlayRef.updatePosition();
    pane.style.visibility = '';
  };

  private onPointerDown = (event: PointerEvent) => {
    const target = event.target;
    if (target instanceof Node && this.overlayRef?.overlayElement.contains(target)) return;
    this.closeSelf('outside', false);
  };

  private onKeyDown = (event: KeyboardEvent) => {
    if (event.key !== 'Escape') return;
    // The reader closes itself on Escape, so the key stops here while the popup is open
    event.preventDefault();
    event.stopPropagation();
    this.closeSelf('escape', true);
  };

  private onScroll = (event: Event) => {
    const target = event.target;
    if (target instanceof Node && this.overlayRef?.overlayElement.contains(target)) return;
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

  private closeSelf(reason: PopupCloseReason, restoreFocus: boolean) {
    const handlers = this.handlers;
    if (this.overlayRef === null) return;
    this.teardown(restoreFocus);
    handlers?.closed(reason);
  }
}

function setCap(pane: HTMLElement, property: string, value: number | undefined) {
  if (value === undefined) pane.style.removeProperty(property);
  else pane.style.setProperty(property, `${value}px`);
}
