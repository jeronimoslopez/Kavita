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
using Kavita.Services.Dictionary;
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
    /// <summary>
    /// Scans, library changes and setting the sidecar URL queue their own detection job, so the worker's sweep only
    /// catches what a job lost to a restart left behind
    /// </summary>
    internal static readonly TimeSpan DetectionInterval = TimeSpan.FromHours(1);

    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
    });
    private readonly Dictionary<(int SeriesId, int ChapterId), List<int>> _readyPages = new();
    private DateTimeOffset _lastReadyEvent = DateTimeOffset.MinValue;
    private DateTimeOffset? _lastDetection;
    /// <summary>
    /// The version the last library-wide refresh compared pages with, or null before the first one
    /// </summary>
    private string? _refreshedVersion;
    /// <summary>
    /// Set while pages a library-wide refresh queued may still be waiting
    /// </summary>
    private bool _refreshQueued;

    public TokenizeWorkerHostedService(IServiceScopeFactory scopeFactory, ILogger<TokenizeWorkerHostedService> logger)
        : this(scopeFactory, logger, TimeProvider.System)
    {
    }

    public void Wake() => _wake.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        if (!await InitializeStoreAsync(stoppingToken)) return;
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

                await FlushReadyPagesAsync(force: true, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[TokenizeWorker] Tokenize pass failed");
            }

            await WaitAsync(wait, stoppingToken);
        }
    }

    /// <summary>
    /// Opens reading.db at startup, so an upgrade that rewrites its rows starts here and doesn't wait for the first
    /// request. Requests that arrive during it are told the store isn't ready
    /// </summary>
    /// <returns>False when Kavita is shutting down</returns>
    internal async Task<bool> InitializeStoreAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var database = scope.ServiceProvider.GetService<ReadingDatabase>();
            if (database != null) await database.InitializeAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            // Each pass opens the file again, which retries the upgrade
            logger.LogError(ex, "[TokenizeWorker] Couldn't open reading.db");
        }
        return true;
    }

    /// <returns>The wait after one more pass the sidecar didn't answer, doubling from a minute up to ten</returns>
    internal static TimeSpan NextBackoff(TimeSpan backoff)
    {
        return backoff == TimeSpan.Zero ? PollInterval : TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
    }

    /// <returns>True when the pass stopped because the sidecar didn't answer</returns>
    internal async Task<bool> RunPassAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        if (_lastDetection == null || now - _lastDetection >= DetectionInterval)
        {
            using var scope = scopeFactory.CreateScope();
            // With no sidecar there's nothing to tokenize with, so the EPUBs aren't opened to count their pages
            var settings = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SettingsRepository.GetSettingsDtoAsync(ct);
            if (string.IsNullOrWhiteSpace(settings.TextSidecarUrl)) return false;

            await scope.ServiceProvider.GetRequiredService<ITokenizeService>().DetectNewFilesAsync();
            _lastDetection = now;
        }

        await RefreshLibraryAsync(ct);

        // Units whose file wasn't on disk this pass. When one is picked again, nothing ahead of it is left to tokenize
        var deferred = new HashSet<long>();
        while (!ct.IsCancellationRequested)
        {
            using var scope = scopeFactory.CreateScope();
            var settings = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SettingsRepository.GetSettingsDtoAsync(ct);
            if (string.IsNullOrWhiteSpace(settings.TextSidecarUrl)) return false;

            var tokenizeService = scope.ServiceProvider.GetRequiredService<ITokenizeService>();
            // Picked again before every page, so a chapter someone just opened jumps ahead of the book in progress
            var unit = await tokenizeService.GetNextUnitAsync(ct);
            if (unit == null && _refreshQueued)
            {
                _refreshQueued = false;
                logger.LogInformation("[TokenizeWorker] Finished tokenizing the library again with {Version}", _refreshedVersion);
            }
            if (unit == null || deferred.Contains(unit.Id)) return false;

            var result = await tokenizeService.ProcessUnitAsync(unit, settings.TextSidecarUrl, ct);
            switch (result.Outcome)
            {
                case TokenizeUnitOutcome.SidecarUnavailable:
                    return true;
                case TokenizeUnitOutcome.FileUnavailable:
                    deferred.Add(unit.Id);
                    break;
                // A page the library-wide refresh queued belongs to a book nobody has open, because opening a chapter
                // raises its pages, so readers aren't told about it
                case TokenizeUnitOutcome.UnitDone when unit.Priority > ReadingPriority.Background:
                    AddReadyPage(result);
                    await FlushReadyPagesAsync(force: false, ct);
                    break;
            }
        }

        return false;
    }

    /// <summary>
    /// Queues the library's pages stored at an older version once per version the sidecar reports, the first time at
    /// startup. A page whose refresh fails goes back to done at its old version, and waits for the next version change
    /// or restart
    /// </summary>
    private async Task RefreshLibraryAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var settings = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SettingsRepository.GetSettingsDtoAsync(ct);
        if (string.IsNullOrWhiteSpace(settings.TextSidecarUrl)) return;

        var refresh = await scope.ServiceProvider.GetRequiredService<ITokenizeService>()
            .RefreshLibraryAsync(settings.TextSidecarUrl, _refreshedVersion, ct);
        if (refresh == null) return;

        _refreshedVersion = refresh.Version;
        if (refresh.Queued == 0) return;
        _refreshQueued = true;
        logger.LogInformation("[TokenizeWorker] Queued {Pages} pages of the library to tokenize again with {Version}",
            refresh.Queued, refresh.Version);
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
