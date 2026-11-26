import {TestBed} from '@angular/core/testing';
import {provideHttpClient} from '@angular/common/http';
import {HttpTestingController, provideHttpClientTesting} from '@angular/common/http/testing';
import {firstValueFrom} from 'rxjs';
import {environment} from '../../environments/environment';
import {DictionaryLookupResult} from '../_models/dictionary/dictionary';
import {DictionaryService} from './dictionary.service';

describe('DictionaryService', () => {
  let service: DictionaryService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({providers: [provideHttpClient(), provideHttpClientTesting()]});
    service = TestBed.inject(DictionaryService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('posts a phrase lookup with the phrase index and returns the phrase entry', async () => {
    const phrase: DictionaryLookupResult = {
      word: {
        id: 9, surface: '気がした', lemma: '気がする', pos: '表現', isProper: false,
        occurrenceReading: null, reading: 'きがする', readings: ['きがする'], jlpt: null,
      },
      senses: [{key: '200:1', pos: ['exp'], gloss: ['to have a feeling that']}],
      analysis: null,
      parts: [],
      partOf: [],
    };
    const answer = firstValueFrom(service.lookup({chapterId: 5, page: 2, block: 4, s: 12, sub: null, phrase: 0}));

    const req = http.expectOne(environment.apiUrl + 'dictionary/lookup');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({chapterId: 5, page: 2, block: 4, s: 12, sub: null, phrase: 0});
    req.flush(phrase);
    expect(await answer).toEqual(phrase);
  });
});
