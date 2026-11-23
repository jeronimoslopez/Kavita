import {TestBed} from '@angular/core/testing';
import {provideHttpClient} from '@angular/common/http';
import {provideHttpClientTesting} from '@angular/common/http/testing';
import {provideRouter} from '@angular/router';
import {TranslocoTestingModule} from '@jsverse/transloco';
import {ToastrService} from '@openng/ngx-toastr';
import {firstValueFrom, of, toArray} from 'rxjs';
import {ActionService} from './action.service';
import {DictionaryService} from './dictionary.service';
import {ConfirmService} from '../shared/confirm.service';
import {Action} from '../_models/actionables/action';
import {ActionItem} from '../_models/actionables/action-item';
import {Series} from '../_models/series';
import {SAVER} from '../_providers/saver.provider';
import {NavService} from './nav.service';

describe('ActionService dictionary series actions', () => {
  const series = {id: 7, name: 'Kokoro', libraryId: 2} as Series;
  let dictionary: {analyzeSeries: ReturnType<typeof vi.fn>, removeSeries: ReturnType<typeof vi.fn>};
  let toastr: {info: ReturnType<typeof vi.fn>, success: ReturnType<typeof vi.fn>};
  let confirm: {confirm: ReturnType<typeof vi.fn>};
  let service: ActionService;

  const item = (action: Action) => ({action} as ActionItem<Series>);
  const toasts = {
    'analysis-queued': 'queued {{name}}',
    'analysis-nothing-queued': 'nothing for {{name}}',
    'confirm-remove-analysis': 'remove {{name}}?',
    'analysis-removed': 'removed {{name}}',
  };

  beforeEach(() => {
    dictionary = {analyzeSeries: vi.fn(), removeSeries: vi.fn(() => of({}))};
    toastr = {info: vi.fn(), success: vi.fn()};
    confirm = {confirm: vi.fn()};

    TestBed.configureTestingModule({
      imports: [TranslocoTestingModule.forRoot({langs: {en: {toasts}}, preloadLangs: true, translocoConfig: {availableLangs: ['en'], defaultLang: 'en'}})],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {provide: DictionaryService, useValue: dictionary},
        {provide: ToastrService, useValue: toastr},
        {provide: ConfirmService, useValue: confirm},
        {provide: SAVER, useValue: vi.fn()},
        {provide: NavService, useValue: {}},
      ],
    });
    service = TestBed.inject(ActionService);
  });

  it('queues analysis and says so', async () => {
    dictionary.analyzeSeries.mockReturnValue(of(3));

    const result = await firstValueFrom(service.handleSeriesAction(item(Action.AnalyzeBook), series));

    expect(dictionary.analyzeSeries).toHaveBeenCalledWith(7);
    expect(toastr.info).toHaveBeenCalledWith('queued Kokoro');
    expect(result.effect).toBe('none');
  });

  it('says nothing was queued when the series has no EPUBs to analyze', async () => {
    dictionary.analyzeSeries.mockReturnValue(of(0));

    await firstValueFrom(service.handleSeriesAction(item(Action.AnalyzeBook), series));

    expect(toastr.info).toHaveBeenCalledWith('nothing for Kokoro');
  });

  it('removes analysis after the admin confirms', async () => {
    confirm.confirm.mockResolvedValue(true);

    await firstValueFrom(service.handleSeriesAction(item(Action.RemoveAnalysis), series));

    expect(confirm.confirm).toHaveBeenCalledWith('remove Kokoro?');
    expect(dictionary.removeSeries).toHaveBeenCalledWith(7);
    expect(toastr.success).toHaveBeenCalledWith('removed Kokoro');
  });

  it('keeps the analysis when the admin cancels', async () => {
    confirm.confirm.mockResolvedValue(false);

    const results = await firstValueFrom(service.handleSeriesAction(item(Action.RemoveAnalysis), series).pipe(toArray()));

    expect(results).toEqual([]);
    expect(dictionary.removeSeries).not.toHaveBeenCalled();
    expect(toastr.success).not.toHaveBeenCalled();
  });
});
