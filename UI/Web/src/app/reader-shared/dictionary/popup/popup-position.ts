/** From this viewport width the popup is a popover on the word, under it a sheet at the bottom of the screen */
export const POPOVER_MIN_WIDTH = 768;

/** Gap between the word and the popover, the sideOffset in shimen's Popover recipe */
export const POPOVER_OFFSET = 12;

/** The popover never comes closer to a viewport edge than this */
export const VIEWPORT_MARGIN = 8;

/** The popover's width, min(380px, 84vw), and its height cap of 72vh, from shimen's Popover recipe */
export const POPOVER_WIDTH = 380;
export const POPOVER_WIDTH_SHARE = 0.84;
export const POPOVER_HEIGHT_SHARE = 0.72;

export interface Box {
  left: number;
  top: number;
  width: number;
  height: number;
}

export interface Size {
  width: number;
  height: number;
}

export type PopupSide = 'below' | 'above' | 'left' | 'right';

export interface Placement {
  /** Viewport coordinates of the popover's top left corner */
  left: number;
  top: number;
  side: PopupSide;
  /** Set when the popover fits on neither side of the word. It scrolls inside this height */
  maxHeight?: number;
  /** The same cap across the lines, in vertical text */
  maxWidth?: number;
}

function clamp(value: number, min: number, max: number): number {
  // When the popup is larger than the room, it sits at the start edge
  return Math.max(min, Math.min(value, max));
}

/**
 * Where the popover goes for a word. In horizontal text it sits below the word, starting at the word's left edge,
 * and flips above when there's no room below. In vertical text it sits to the left of the word, the way the lines
 * advance, and falls back to the right. When neither side fits, it takes the side with more room and is capped to
 * that room, so it never covers the word. The other axis is clamped so the popover stays on screen
 */
export function placePopover(word: Box, viewport: Size, popup: Size, vertical: boolean): Placement {
  const m = VIEWPORT_MARGIN;
  if (vertical) {
    const rightX = word.left + word.width + POPOVER_OFFSET;
    const roomLeft = Math.max(0, word.left - POPOVER_OFFSET - m);
    const roomRight = Math.max(0, viewport.width - rightX - m);
    let side: PopupSide;
    let width = popup.width;
    if (width <= roomLeft) side = 'left';
    else if (width <= roomRight) side = 'right';
    else {
      side = roomLeft >= roomRight ? 'left' : 'right';
      width = side === 'left' ? roomLeft : roomRight;
    }
    const left = side === 'left' ? word.left - POPOVER_OFFSET - width : rightX;
    const top = clamp(word.top, m, viewport.height - popup.height - m);
    return width < popup.width ? {left, top, side, maxWidth: width} : {left, top, side};
  }

  const belowY = word.top + word.height + POPOVER_OFFSET;
  const roomBelow = Math.max(0, viewport.height - belowY - m);
  const roomAbove = Math.max(0, word.top - POPOVER_OFFSET - m);
  let side: PopupSide;
  let height = popup.height;
  if (height <= roomBelow) side = 'below';
  else if (height <= roomAbove) side = 'above';
  else {
    side = roomBelow >= roomAbove ? 'below' : 'above';
    height = side === 'below' ? roomBelow : roomAbove;
  }
  const top = side === 'below' ? belowY : word.top - POPOVER_OFFSET - height;
  const left = clamp(word.left, m, viewport.width - popup.width - m);
  return height < popup.height ? {left, top, side, maxHeight: height} : {left, top, side};
}

/** True when no part of the word is on screen. A range whose nodes left the page measures as an empty box */
export function isOutOfView(word: Box, viewport: Size): boolean {
  if (word.width === 0 && word.height === 0) return true;
  return word.top + word.height < 0 || word.top > viewport.height
    || word.left + word.width < 0 || word.left > viewport.width;
}
