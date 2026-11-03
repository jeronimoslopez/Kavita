import {MangaFormat} from './manga-format';
import {OcrStatus} from './ocr-status';

export interface MangaFile {
  id: number;
  filePath: string;
  pages: number;
  format: MangaFormat;
  created: string;
  bytes: number;
  koreaderHash: string;
  ocrStatus: OcrStatus;
}
