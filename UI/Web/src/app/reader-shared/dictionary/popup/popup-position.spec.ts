import {isOutOfView, placePopover, POPOVER_OFFSET, VIEWPORT_MARGIN} from './popup-position';

const viewport = {width: 1280, height: 800};
const popup = {width: 380, height: 300};

function word(left: number, top: number, width = 40, height = 24) {
  return {left, top, width, height};
}

describe('placePopover in horizontal text', () => {
  it('goes below the word, starting at its left edge', () => {
    expect(placePopover(word(200, 100), viewport, popup, false))
      .toEqual({left: 200, top: 100 + 24 + POPOVER_OFFSET, side: 'below'});
  });

  it('flips above when there is no room below', () => {
    expect(placePopover(word(200, 600), viewport, popup, false))
      .toEqual({left: 200, top: 600 - POPOVER_OFFSET - 300, side: 'above'});
  });

  it('takes the side with more room when neither fits, and caps its height to that room', () => {
    const below = placePopover(word(200, 100), {width: 1280, height: 500}, {width: 380, height: 450}, false);
    const belowY = 100 + 24 + POPOVER_OFFSET;
    expect(below).toEqual({left: 200, top: belowY, side: 'below', maxHeight: 500 - belowY - VIEWPORT_MARGIN});

    const above = placePopover(word(200, 380), {width: 1280, height: 500}, {width: 380, height: 450}, false);
    const room = 380 - POPOVER_OFFSET - VIEWPORT_MARGIN;
    expect(above).toEqual({left: 200, top: VIEWPORT_MARGIN, side: 'above', maxHeight: room});
    // The capped popover ends above the word, not over it
    expect(above.top + room).toBe(380 - POPOVER_OFFSET);
  });

  it('moves left to stay on screen near the right edge', () => {
    expect(placePopover(word(1200, 100), viewport, popup, false).left).toBe(1280 - 380 - VIEWPORT_MARGIN);
  });
});

describe('placePopover in vertical text', () => {
  it('goes to the left of the word, starting at its top', () => {
    expect(placePopover(word(900, 150, 24, 60), viewport, popup, true))
      .toEqual({left: 900 - POPOVER_OFFSET - 380, top: 150, side: 'left'});
  });

  it('falls back to the right when there is no room on the left', () => {
    expect(placePopover(word(100, 150, 24, 60), viewport, popup, true))
      .toEqual({left: 100 + 24 + POPOVER_OFFSET, top: 150, side: 'right'});
  });

  it('caps its width to the larger side when neither side fits', () => {
    const place = placePopover(word(300, 150, 24, 60), {width: 600, height: 800}, popup, true);
    const room = 300 - POPOVER_OFFSET - VIEWPORT_MARGIN;
    expect(place).toEqual({left: VIEWPORT_MARGIN, top: 150, side: 'left', maxWidth: room});
  });

  it('moves up to stay on screen near the bottom', () => {
    expect(placePopover(word(900, 700, 24, 60), viewport, popup, true).top).toBe(800 - 300 - VIEWPORT_MARGIN);
  });
});

describe('isOutOfView', () => {
  it('is false for a word partly on screen', () => {
    expect(isOutOfView(word(100, -10), viewport)).toBe(false);
  });

  it('is true once the word scrolls past any edge', () => {
    expect(isOutOfView(word(100, -30), viewport)).toBe(true);
    expect(isOutOfView(word(100, 801), viewport)).toBe(true);
    expect(isOutOfView(word(-50, 100), viewport)).toBe(true);
    expect(isOutOfView(word(1281, 100), viewport)).toBe(true);
  });

  it('is true for an empty box, which is what a range left without its nodes measures', () => {
    expect(isOutOfView({left: 0, top: 0, width: 0, height: 0}, viewport)).toBe(true);
  });
});
