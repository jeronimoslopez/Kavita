import {DictionaryLookupResult} from '../../../_models/dictionary/dictionary';

/** What the popup shows for one lookup. `empty` is a lookup the server answered with nothing to look up */
export type DictionaryPopupState =
  | {kind: 'loading'}
  | {kind: 'failed'}
  | {kind: 'empty'}
  | {kind: 'result', result: DictionaryLookupResult};

/** Everything the popup renders, which the controller replaces on every change */
export interface DictionaryPopupView {
  state: DictionaryPopupState;
  /** The tapped text, shown as the headword until the lookup answers */
  surface: string;
  canGoBack: boolean;
  /** The part whose entry is open, so its chip shows as pressed. Null for the whole word */
  openSub: number | null;
  /** The `partOf` phrase whose entry is open, so its chip shows as pressed. Null for the word or a part */
  openPhrase: number | null;
  /** The key of the reading group whose tab is selected, from groupKey. Null selects the first group */
  openGroup: string | null;
  /** The entry is the tapped word on a page whose series is known, so a reading can be saved for it */
  canChoose: boolean;
}

export type DictionaryPopupMode = 'popover' | 'sheet';
