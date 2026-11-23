using Kavita.API.Database;
using Kavita.API.Services;
using Kavita.API.Services.SignalR;
using Kavita.Models.DTOs.Dictionary;
using Kavita.Models.DTOs.Settings;
using Kavita.Models.DTOs.SignalR;
using Kavita.Models.DTOs.SignalR.Bodies;
using Kavita.Services.HostedServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Kavita.Services.Tests.Dictionary;

public class TokenizeWorkerHostedServiceTests
{
    private readonly ServerSettingDto _settings = new() { TextSidecarUrl = "http://text:8000" };
    private readonly ITokenizeService _tokenizeService = Substitute.For<ITokenizeService>();
    private readonly IEventHub _eventHub = Substitute.For<IEventHub>();
    private readonly ManualClock _time = new();
    private readonly TokenizeWorkerHostedService _worker;

    public TokenizeWorkerHostedServiceTests()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.SettingsRepository.GetSettingsDtoAsync(Arg.Any<CancellationToken>()).Returns(_ => _settings);

        var services = new ServiceCollection();
        services.AddSingleton(unitOfWork);
        services.AddSingleton(_tokenizeService);
        services.AddSingleton(_eventHub);
        var provider = services.BuildServiceProvider();

        _worker = new TokenizeWorkerHostedService(provider.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<ILogger<TokenizeWorkerHostedService>>(), _time);
    }

    private static ReadingUnitRef Unit(int page) => new(page + 1, ReadingUnitKind.Epub, 3, page, "1", 0);

    [Fact]
    public async Task RunPass_NoSidecarUrl_StaysIdle()
    {
        _settings.TextSidecarUrl = string.Empty;

        var unavailable = await _worker.RunPassAsync(CancellationToken.None);

        Assert.False(unavailable);
        await _tokenizeService.DidNotReceive().DetectNewFilesAsync();
        await _tokenizeService.DidNotReceiveWithAnyArgs().GetNextUnitAsync();
    }

    [Fact]
    public async Task RunPass_TokenizesUntilTheQueueIsEmpty()
    {
        _tokenizeService.GetNextUnitAsync(Arg.Any<CancellationToken>()).Returns(Unit(0), Unit(1), null);
        _tokenizeService.ProcessUnitAsync(Arg.Any<ReadingUnitRef>(), "http://text:8000", Arg.Any<CancellationToken>())
            .Returns(call => new TokenizeUnitResult(TokenizeUnitOutcome.UnitDone, 1, 2, call.Arg<ReadingUnitRef>().Page));

        var unavailable = await _worker.RunPassAsync(CancellationToken.None);
        await _worker.FlushReadyPagesAsync(force: true, CancellationToken.None);

        Assert.False(unavailable);
        await _tokenizeService.Received(1).DetectNewFilesAsync();
        await _tokenizeService.ReceivedWithAnyArgs(2).ProcessUnitAsync(default!, default!);
        // The first page goes out at once, and the second waits for the next flush. Pages stay 0-based
        await _eventHub.Received(1).SendMessageAsync(MessageFactory.DictionaryPagesReady,
            Arg.Is<SignalRMessageDto>(m => ((DictionaryPagesReadyEventBodyDto) m.Body!).Pages.SequenceEqual(new[] { 0 })),
            false, Arg.Any<CancellationToken>());
        await _eventHub.Received(1).SendMessageAsync(MessageFactory.DictionaryPagesReady,
            Arg.Is<SignalRMessageDto>(m => ((DictionaryPagesReadyEventBodyDto) m.Body!).Pages.SequenceEqual(new[] { 1 })),
            false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunPass_SidecarUnavailable_StopsThePass()
    {
        _tokenizeService.GetNextUnitAsync(Arg.Any<CancellationToken>()).Returns(Unit(0));
        _tokenizeService.ProcessUnitAsync(Arg.Any<ReadingUnitRef>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TokenizeUnitResult(TokenizeUnitOutcome.SidecarUnavailable));

        var unavailable = await _worker.RunPassAsync(CancellationToken.None);

        Assert.True(unavailable);
        await _tokenizeService.ReceivedWithAnyArgs(1).ProcessUnitAsync(default!, default!);
    }

    [Fact]
    public async Task RunPass_UrlClearedMidPass_StopsBeforeTheNextUnit()
    {
        _tokenizeService.GetNextUnitAsync(Arg.Any<CancellationToken>()).Returns(Unit(0));
        _tokenizeService.ProcessUnitAsync(Arg.Any<ReadingUnitRef>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _settings.TextSidecarUrl = string.Empty;
                return new TokenizeUnitResult(TokenizeUnitOutcome.UnitFailed);
            });

        var unavailable = await _worker.RunPassAsync(CancellationToken.None);

        Assert.False(unavailable);
        await _tokenizeService.ReceivedWithAnyArgs(1).ProcessUnitAsync(default!, default!);
    }

    [Fact]
    public void NextBackoff_DoublesFromOneMinuteUpToTen()
    {
        var waits = new List<TimeSpan>();
        var backoff = TimeSpan.Zero;
        for (var i = 0; i < 6; i++)
        {
            backoff = TokenizeWorkerHostedService.NextBackoff(backoff);
            waits.Add(backoff);
        }

        Assert.Equal(new[] { 1, 2, 4, 8, 10, 10 }.Select(m => TimeSpan.FromMinutes(m)), waits);
    }

    [Fact]
    public async Task Flush_WithinFiveSeconds_WaitsUnlessForced()
    {
        _tokenizeService.GetNextUnitAsync(Arg.Any<CancellationToken>()).Returns(Unit(0), Unit(1), Unit(2), null);
        _tokenizeService.ProcessUnitAsync(Arg.Any<ReadingUnitRef>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                // Each page takes six seconds, so every page after the first clears the five-second gap
                _time.Advance(TimeSpan.FromSeconds(6));
                return new TokenizeUnitResult(TokenizeUnitOutcome.UnitDone, 1, 2, call.Arg<ReadingUnitRef>().Page);
            });

        await _worker.RunPassAsync(CancellationToken.None);

        await _eventHub.ReceivedWithAnyArgs(3).SendMessageAsync(default!, default!, default);
    }

    /// <summary>
    /// A clock that only moves when a test moves it. The flush reads only the time, so no timers are needed
    /// </summary>
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
