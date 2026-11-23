import {DictionaryClick, DictionaryClickAction, dictionaryClickAction} from './dictionary-click-action';

const cases: [string, DictionaryClick, DictionaryClickAction][] = [
  ['a word on the page opens the popup', {onPage: true, onWord: true, dismissedPopup: false}, 'lookup'],
  ['a word opens the popup even when the same tap closed it', {onPage: true, onWord: true, dismissedPopup: true}, 'lookup'],
  ['the tap that closed the popup is dropped off a word', {onPage: true, onWord: false, dismissedPopup: true}, 'drop'],
  ['any other click on the page passes to its own handler', {onPage: true, onWord: false, dismissedPopup: false}, 'pass'],
  ['a control outside the content is released', {onPage: false, onWord: false, dismissedPopup: false}, 'release'],
  ['a control is released even when the same tap closed the popup', {onPage: false, onWord: false, dismissedPopup: true}, 'release'],
];

describe('dictionaryClickAction', () => {
  for (const [name, click, action] of cases) {
    it(name, () => {
      expect(dictionaryClickAction(click)).toBe(action);
    });
  }
});
