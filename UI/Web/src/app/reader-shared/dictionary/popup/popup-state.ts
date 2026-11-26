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
}

export type DictionaryPopupMode = 'popover' | 'sheet';
