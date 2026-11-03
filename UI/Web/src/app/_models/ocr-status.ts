export enum OcrStatus {
  NotChecked = 0,
  HasText = 1,
  Pending = 2,
  Processing = 3,
  Done = 4,
  Failed = 5,
  Removed = 6,
}

/** Translation key suffix for each status, under details-tab */
export const ocrStatusKeys: Record<OcrStatus, string> = {
  [OcrStatus.NotChecked]: 'ocr-not-checked',
  [OcrStatus.HasText]: 'ocr-has-text',
  [OcrStatus.Pending]: 'ocr-pending',
  [OcrStatus.Processing]: 'ocr-processing',
  [OcrStatus.Done]: 'ocr-done',
  [OcrStatus.Failed]: 'ocr-failed',
  [OcrStatus.Removed]: 'ocr-removed',
};
