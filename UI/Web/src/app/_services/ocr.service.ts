import {HttpClient} from '@angular/common/http';
import {inject, Injectable} from '@angular/core';
import {environment} from '../../environments/environment';
import {OcrChapterStatus, OcrPage} from '../_models/ocr/ocr';

@Injectable({
  providedIn: 'root'
})
export class OcrService {

  private readonly httpClient = inject(HttpClient);
  private readonly baseUrl = environment.apiUrl;

  getChapterStatus(chapterId: number) {
    return this.httpClient.get<OcrChapterStatus | null>(this.baseUrl + 'ocr/chapter?chapterId=' + chapterId);
  }

  /** @param pageNum 1-based, matching pdf.js */
  getPage(chapterId: number, pageNum: number) {
    return this.httpClient.get<OcrPage | null>(this.baseUrl + `ocr/page?chapterId=${chapterId}&pageNum=${pageNum}`);
  }

  prioritizeChapter(chapterId: number) {
    return this.httpClient.post(this.baseUrl + 'ocr/chapter/prioritize?chapterId=' + chapterId, {});
  }

  /** @returns How many files were queued */
  runSeries(seriesId: number, replaceExistingText: boolean) {
    return this.httpClient.post<number>(this.baseUrl + 'ocr/series/run', {seriesId, replaceExistingText});
  }

  removeSeries(seriesId: number) {
    return this.httpClient.post(this.baseUrl + 'ocr/series/remove', {seriesId, replaceExistingText: false});
  }
}
