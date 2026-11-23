using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kavita.API.Services;
using Kavita.Models.DTOs.Dictionary;

namespace Kavita.Services.Dictionary;

public class DictionaryService : IDictionaryService
{
    public Task<EpubPageTokensDto?> GetEpubPageAsync(int chapterId, int page, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task<DictionaryLookupResultDto?> LookupAsync(DictionaryLookupRequestDto request, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task PrioritizeChapterAsync(int chapterId, CancellationToken ct = default) => throw new NotImplementedException();

    public Task<int> AnalyzeSeriesAsync(int seriesId, CancellationToken ct = default) => throw new NotImplementedException();

    public Task RemoveSeriesAsync(int seriesId, CancellationToken ct = default) => throw new NotImplementedException();

    public Task<IList<AnalysisPassageDto>> GetPendingAnalysisAsync(int limit, int minPriority, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task<AnalysisStoreResultDto> StoreAnalysisAsync(AnalysisStoreRequestDto request, CancellationToken ct = default) =>
        throw new NotImplementedException();
}
