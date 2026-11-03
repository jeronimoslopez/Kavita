import {OcrStatus} from '../ocr-status';

/** Which of the sidecar's engines reads a library's pages */
export enum OcrEngine {
  /** Manga for Manga and Comic libraries, Books for the rest */
  Auto = 0,
  /** NDLOCR-Lite */
  Books = 1,
  /** PP-OCRv6_manga */
  Manga = 2,
}

export enum OcrLineKind {
  Body = 0,
  Title = 1,
  Caption = 2,
  Ruby = 3,
  Header = 4,
  Folio = 5,
}

/** One OCR line. The box is a fraction of the page image, origin top left */
export interface OcrLine {
  order: number;
  text: string;
  x: number;
  y: number;
  width: number;
  height: number;
  vertical: boolean;
  kind: OcrLineKind;
  detectionConfidence: number;
  recognitionConfidence: number;
  /** Lines with the same number are one speech bubble or caption. Only the manga engine groups lines */
  block?: number | null;
}

export interface OcrPage {
  /** 1-based, matching pdf.js */
  pageNumber: number;
  /** Changes when the page is OCR'd again */
  version: number;
  lines: OcrLine[];
}

export interface OcrChapterStatus {
  status: OcrStatus;
  /** 1-based page numbers that have OCR text */
  pages: number[];
  pendingPages: number;
  failedPages: number;
}
