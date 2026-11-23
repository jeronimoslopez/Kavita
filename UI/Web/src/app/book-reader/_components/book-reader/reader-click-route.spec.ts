import {ReaderClick, ReaderClickRoute, routeReaderClick} from './reader-click-route';

const PLAIN: ReaderClick = {
  dictionary: false, dismissedPopup: false, onWord: false, onLink: false, hasSelection: false, onHighlight: false,
};

const cases: [string, Partial<ReaderClick>, ReaderClickRoute][] = [
  // Dictionary on
  ['a word opens the dictionary', {dictionary: true, onWord: true}, 'dictionary'],
  ['a word inside a highlight opens the dictionary, not the annotation', {dictionary: true, onWord: true, onHighlight: true}, 'dictionary'],
  ['a part of a highlight that is not a word opens the annotation', {dictionary: true, onHighlight: true}, 'annotation'],
  ['a link follows the link', {dictionary: true, onLink: true}, 'link'],
  ['a link inside a highlight follows the link', {dictionary: true, onLink: true, onHighlight: true}, 'link'],
  ['empty space goes to the reader', {dictionary: true}, 'reader'],
  ['a click with text selected goes to the reader', {dictionary: true, hasSelection: true}, 'reader'],
  ['the click that dismissed the popup is dropped', {dictionary: true, dismissedPopup: true}, 'drop'],
  ['the click that dismissed the popup over a link is dropped', {dictionary: true, dismissedPopup: true, onLink: true}, 'drop'],
  ['the click that dismissed the popup over a highlight is dropped', {dictionary: true, dismissedPopup: true, onHighlight: true}, 'drop'],
  // Dictionary off, so Yomitan answers onWord
  ['a word is left to Yomitan', {onWord: true}, 'yomitan'],
  ['a highlight opens the annotation', {onHighlight: true}, 'annotation'],
  ['a link follows the link', {onLink: true}, 'link'],
  ['empty space goes to the reader', {}, 'reader'],
  ['a click with text selected goes to the reader', {hasSelection: true}, 'reader'],
  ['a dismissal flag is ignored without the dictionary', {dismissedPopup: true}, 'reader'],
];

describe('routeReaderClick', () => {
  for (const [name, click, route] of cases) {
    const mode = click.dictionary ? 'dictionary on' : 'dictionary off';
    it(`${mode}: ${name}`, () => {
      expect(routeReaderClick({...PLAIN, ...click})).toBe(route);
    });
  }
});
