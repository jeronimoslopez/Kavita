using System;
using System.Threading;
using System.Threading.Tasks;
using Kavita.API.Services;
using Kavita.Models.DTOs.Dictionary;

namespace Kavita.Services.Dictionary;

public class TokenizeService : ITokenizeService
{
    public Task MarkSeriesForCheckAsync(int seriesId, CancellationToken ct = default) => throw new NotImplementedException();

    public Task DetectNewFilesAsync() => throw new NotImplementedException();

    public Task<ReadingUnitRef?> GetNextUnitAsync(CancellationToken ct = default) => throw new NotImplementedException();

    public Task<TokenizeUnitResult> ProcessUnitAsync(ReadingUnitRef unit, string sidecarUrl, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task<PrioritizedFiles> PrioritizeChapterAsync(int chapterId, CancellationToken ct = default) =>
        throw new NotImplementedException();
}
