using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kavita.API.Services;
using Kavita.Models.DTOs.Dictionary;

namespace Kavita.Services.Dictionary;

public class ReadingStore : IReadingStore
{
    public Task<IReadOnlyList<long>> UpsertWordsAsync(IReadOnlyList<SidecarWordDto> words, string lexiconVersion,
        CancellationToken ct = default) => throw new NotImplementedException();

    public Task<IReadOnlyDictionary<long, StoredWord>> GetWordsAsync(IReadOnlyCollection<long> ids,
        CancellationToken ct = default) => throw new NotImplementedException();

    public Task<IReadOnlyDictionary<int, string>> GetFileStampsAsync(CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task SeedFileAsync(int mangaFileId, int pageCount, string sourceStamp, int priority,
        CancellationToken ct = default) => throw new NotImplementedException();

    public Task DeleteFilesAsync(IReadOnlyCollection<int> mangaFileIds, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task<ReadingUnitRef?> GetNextPendingUnitAsync(CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task<bool> SaveTokenizedUnitAsync(long unitId, string sourceStamp, string tokenizerVersion,
        IReadOnlyList<StoredBlock> blocks, IReadOnlyList<AnalysisSpan> ambiguous, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task<ReadingUnitState> RecordFailedAttemptAsync(long unitId, int maxAttempts, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task<ReadingUnit?> GetUnitAsync(int mangaFileId, int page, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task RaiseUnitPriorityAsync(int mangaFileId, int? page, int priority, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<StoredAnalysis>> GetUnitAnalysisAsync(long unitId, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task RequestAnalysisAsync(IReadOnlyCollection<int> mangaFileIds, int priority, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task RaiseAnalysisPriorityAsync(int mangaFileId, int? page, int? block, int priority,
        CancellationToken ct = default) => throw new NotImplementedException();

    public Task<IReadOnlyList<PendingAnalysisRow>> GetPendingAnalysisAsync(int limit, int minPriority,
        CancellationToken ct = default) => throw new NotImplementedException();

    public Task<AnalysisStoreOutcome> StoreAnalysisAsync(string generator, string promptVersion,
        IReadOnlyList<AnalysisAnswer> answers, CancellationToken ct = default) => throw new NotImplementedException();

    public Task ClearAnalysisAsync(IReadOnlyCollection<int> mangaFileIds, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task<ReadingStoreCounts> GetCountsAsync(IReadOnlyCollection<int>? mangaFileIds, CancellationToken ct = default) =>
        throw new NotImplementedException();
}
