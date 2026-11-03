using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Kavita.API.Database;
using Kavita.API.Services;
using Kavita.API.Services.SignalR;
using Kavita.Models.DTOs.SignalR;
using Kavita.Services.Ocr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kavita.Services.HostedServices;

/// <summary>
/// OCRs queued PDF pages one at a time with the sidecar. It runs outside Hangfire because a library's first pass can take
/// hours, and Hangfire's in-memory queue forgets its jobs on restart. The queue lives in the database instead
/// </summary>
public class OcrWorkerHostedService(IServiceScopeFactory scopeFactory, ILogger<OcrWorkerHostedService> logger,
    TimeProvider timeProvider) : BackgroundService, IOcrWorkerSignal
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(10);
    /// <summary>
    /// Each pages-ready event costs an access check per online user, so pages are sent in batches
    /// </summary>
    private static readonly TimeSpan ReadyEventInterval = TimeSpan.FromSeconds(5);

    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
    });
    private readonly Dictionary<(int SeriesId, int ChapterId), List<int>> _readyPages = new();
    private DateTimeOffset _lastReadyEvent = DateTimeOffset.MinValue;

    public OcrWorkerHostedService(IServiceScopeFactory scopeFactory, ILogger<OcrWorkerHostedService> logger)
        : this(scopeFactory, logger, TimeProvider.System)
    {
    }

    public void Wake() => _wake.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var backoff = TimeSpan.Zero;

        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = PollInterval;
            try
            {
                var unavailable = await RunPassAsync(stoppingToken);
                if (unavailable)
                {
                    backoff = backoff == TimeSpan.Zero ? PollInterval : TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
                    wait = backoff;
                    logger.LogWarning("[OcrWorker] Sidecar unavailable, trying again in {Minutes} minutes", wait.TotalMinutes);
                }
                else
                {
                    backoff = TimeSpan.Zero;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[OcrWorker] OCR pass failed");
            }

            await FlushReadyPagesAsync(force: true, stoppingToken);
            await WaitAsync(wait, stoppingToken);
        }
    }

    /// <returns>True when the pass stopped because the sidecar didn't answer</returns>
    private async Task<bool> RunPassAsync(CancellationToken ct)
    {
        using (var scope = scopeFactory.CreateScope())
        {
            // Files the scan or a library change marked unchecked, in case their Hangfire job was lost to a restart
            await scope.ServiceProvider.GetRequiredService<IOcrService>().DetectNewFilesAsync();
        }

        while (!ct.IsCancellationRequested)
        {
            using var scope = scopeFactory.CreateScope();
            var settings = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SettingsRepository.GetSettingsDtoAsync(ct);
            if (string.IsNullOrWhiteSpace(settings.OcrSidecarUrl)) return false;

            var now = TimeOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
            if (!OcrSchedule.IsWithinWindow(now, settings.OcrWindowStart, settings.OcrWindowEnd)) return false;

            var ocrService = scope.ServiceProvider.GetRequiredService<IOcrService>();
            // Picked again before every page, so a book someone just opened jumps ahead of the one in progress
            var fileId = await ocrService.GetNextFileIdAsync(ct);
            if (fileId == null) return false;

            var result = await ocrService.ProcessNextPageAsync(fileId.Value, settings.OcrSidecarUrl, ct);
            switch (result.Outcome)
            {
                case OcrPageOutcome.SidecarUnavailable:
                    return true;
                case OcrPageOutcome.PageDone:
                    AddReadyPage(result);
                    await FlushReadyPagesAsync(force: false, ct);
                    break;
                case OcrPageOutcome.FileFinished:
                case OcrPageOutcome.FileChanged:
                    await FlushReadyPagesAsync(force: true, ct);
                    break;
            }
        }

        return false;
    }

    private void AddReadyPage(OcrPageResult result)
    {
        var key = (result.SeriesId, result.ChapterId);
        if (!_readyPages.TryGetValue(key, out var pages)) _readyPages[key] = pages = [];
        pages.Add(result.PageNum);
    }

    private async Task FlushReadyPagesAsync(bool force, CancellationToken ct)
    {
        if (_readyPages.Count == 0) return;
        var now = timeProvider.GetUtcNow();
        if (!force && now - _lastReadyEvent < ReadyEventInterval) return;

        using var scope = scopeFactory.CreateScope();
        var eventHub = scope.ServiceProvider.GetRequiredService<IEventHub>();
        foreach (var ((seriesId, chapterId), pages) in _readyPages)
        {
            await eventHub.SendMessageAsync(MessageFactory.OcrPagesReady,
                MessageFactory.OcrPagesReadyEvent(seriesId, chapterId, pages.Distinct().Order().ToList()), false, ct);
        }

        _readyPages.Clear();
        _lastReadyEvent = now;
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delayTask = Task.Delay(delay, timeProvider, timeout.Token);
            var wakeTask = _wake.Reader.ReadAsync(timeout.Token).AsTask();
            await Task.WhenAny(delayTask, wakeTask);
            await timeout.CancelAsync();
        }
        catch (OperationCanceledException)
        {
            // Shutting down, or woken early
        }
    }
}
