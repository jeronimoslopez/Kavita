import {HttpClient} from '@angular/common/http';
import {inject, Injectable} from '@angular/core';
import {environment} from '../../environments/environment';
import {DictionaryLookupRequest, DictionaryLookupResult, EpubPageTokens} from '../_models/dictionary/dictionary';

@Injectable({
  providedIn: 'root'
})
export class DictionaryService {

  private readonly httpClient = inject(HttpClient);
  private readonly baseUrl = environment.apiUrl;

  /**
   * @param page 0-based, as the book reader counts pages
   * @returns Null when the page isn't tokenized yet. Wait for DictionaryPagesReady and ask again
   */
  getEpubPage(chapterId: number, page: number) {
    return this.httpClient.get<EpubPageTokens | null>(this.baseUrl + `dictionary/epub-page?chapterId=${chapterId}&page=${page}`);
  }

  /** @returns Null when nothing can be looked up at that offset */
  lookup(request: DictionaryLookupRequest) {
    return this.httpClient.post<DictionaryLookupResult | null>(this.baseUrl + 'dictionary/lookup', request);
  }

  /** Call when a chapter opens, so it and the next chapter are tokenized first */
  prioritizeChapter(chapterId: number) {
    return this.httpClient.post(this.baseUrl + 'dictionary/chapter/prioritize?chapterId=' + chapterId, {});
  }

  /** @returns How many files were queued for analysis */
  analyzeSeries(seriesId: number) {
    return this.httpClient.post<number>(this.baseUrl + 'dictionary/series/analyze', {seriesId});
  }

  removeSeries(seriesId: number) {
    return this.httpClient.post(this.baseUrl + 'dictionary/series/remove', {seriesId});
  }
}
