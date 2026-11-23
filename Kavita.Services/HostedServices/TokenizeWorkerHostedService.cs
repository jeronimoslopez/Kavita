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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kavita.Services.HostedServices;

/// <summary>
/// Tokenizes pending reading.db units one page at a time with the text sidecar. Like the OCR worker it runs outside
/// Hangfire, and its queue is the pending unit rows
/// </summary>
public class TokenizeWorkerHostedService(IServiceScopeFactory scopeFactory, ILogger<TokenizeWorkerHostedService> logger,
    TimeProvider timeProvider) : BackgroundService, ITokenizeWorkerSignal
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(10);
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

    public TokenizeWorkerHostedService(IServiceScopeFactory scopeFactory, ILogger<TokenizeWorkerHostedService> logger)
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
                    backoff = NextBackoff(backoff);
                    wait = backoff;
                    logger.LogWarning("[TokenizeWorker] Text sidecar unavailable, trying again in {Minutes} minutes", wait.TotalMinutes);
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
                logger.LogError(ex, "[TokenizeWorker] Tokenize pass failed");
            }

            await FlushReadyPagesAsync(force: true, stoppingToken);
            await WaitAsync(wait, stoppingToken);
        }
    }

    /// <returns>The wait after one more pass the sidecar didn't answer, doubling from a minute up to ten</returns>
    internal static TimeSpan NextBackoff(TimeSpan backoff)
    {
        return backoff == TimeSpan.Zero ? PollInterval : TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
    }

    /// <returns>True when the pass stopped because the sidecar didn't answer</returns>
    internal async Task<bool> RunPassAsync(CancellationToken ct)
    {
        using (var scope = scopeFactory.CreateScope())
        {
            // With no sidecar there's nothing to tokenize with, so the EPUBs aren't opened to count their pages
            var settings = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SettingsRepository.GetSettingsDtoAsync(ct);
            if (string.IsNullOrWhiteSpace(settings.TextSidecarUrl)) return false;

            // Files a scan or a library change should have seeded, in case their Hangfire job was lost to a restart
            await scope.ServiceProvider.GetRequiredService<ITokenizeService>().DetectNewFilesAsync();
        }

        while (!ct.IsCancellationRequested)
        {
            using var scope = scopeFactory.CreateScope();
            var settings = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SettingsRepository.GetSettingsDtoAsync(ct);
            if (string.IsNullOrWhiteSpace(settings.TextSidecarUrl)) return false;

            var tokenizeService = scope.ServiceProvider.GetRequiredService<ITokenizeService>();
            // Picked again before every page, so a chapter someone just opened jumps ahead of the book in progress
            var unit = await tokenizeService.GetNextUnitAsync(ct);
            if (unit == null) return false;

            var result = await tokenizeService.ProcessUnitAsync(unit, settings.TextSidecarUrl, ct);
            switch (result.Outcome)
            {
                case TokenizeUnitOutcome.SidecarUnavailable:
                    return true;
                case TokenizeUnitOutcome.UnitDone:
                    AddReadyPage(result);
                    await FlushReadyPagesAsync(force: false, ct);
                    break;
            }
        }

        return false;
    }

    private void AddReadyPage(TokenizeUnitResult result)
    {
        var key = (result.SeriesId, result.ChapterId);
        if (!_readyPages.TryGetValue(key, out var pages)) _readyPages[key] = pages = [];
        pages.Add(result.Page);
    }

    internal async Task FlushReadyPagesAsync(bool force, CancellationToken ct)
    {
        if (_readyPages.Count == 0) return;
        var now = timeProvider.GetUtcNow();
        if (!force && now - _lastReadyEvent < ReadyEventInterval) return;

        using var scope = scopeFactory.CreateScope();
        var eventHub = scope.ServiceProvider.GetRequiredService<IEventHub>();
        foreach (var ((seriesId, chapterId), pages) in _readyPages)
        {
            await eventHub.SendMessageAsync(MessageFactory.DictionaryPagesReady,
                MessageFactory.DictionaryPagesReadyEvent(seriesId, chapterId, pages.Distinct().Order().ToList()), false, ct);
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
