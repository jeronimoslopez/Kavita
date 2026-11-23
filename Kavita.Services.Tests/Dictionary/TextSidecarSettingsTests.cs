using Hangfire;
using Hangfire.Storage;
using Kavita.API.Services;
using Kavita.API.Services.Scanner;
using Kavita.Common;
using Kavita.Database.Tests;
using Kavita.Services.Tests.Helpers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit.Abstractions;

namespace Kavita.Services.Tests.Dictionary;

[Collection(HangfireStorageCollection.Name)]
public class TextSidecarSettingsTests(ITestOutputHelper outputHelper) : AbstractDbTest(outputHelper)
{
    private readonly ITokenizeWorkerSignal _signal = Substitute.For<ITokenizeWorkerSignal>();

    private SettingsService CreateSettingsService(Kavita.API.Database.IUnitOfWork unitOfWork) =>
        new(unitOfWork, Substitute.For<IDirectoryService>(), Substitute.For<ILibraryWatcher>(),
            Substitute.For<ITaskScheduler>(), Substitute.For<ILogger<SettingsService>>(), Substitute.For<IOidcService>(),
            Substitute.For<ILoggingService>(), _signal);

    private static int QueuedDetections()
    {
        var monitor = JobStorage.Current.GetMonitoringApi();
        return monitor.EnqueuedJobs("default", 0, 100)
            .Count(j => j.Value.Job?.Type == typeof(ITokenizeService) && j.Value.Job.Method.Name == nameof(ITokenizeService.DetectNewFilesAsync));
    }

    [Fact]
    public async Task UpdateSettings_StoresTheTextSidecarUrlAndRejectsBadValues()
    {
        var (unitOfWork, _, _) = await CreateDatabase();
        var settingsService = CreateSettingsService(unitOfWork);

        var settings = await unitOfWork.SettingsRepository.GetSettingsDtoAsync();
        Assert.Equal(string.Empty, settings.TextSidecarUrl);
        Assert.False(settings.DictionaryAnalysisOnOpen);
        settings.TextSidecarUrl = " http://text:8000/ ";
        settings.DictionaryAnalysisOnOpen = true;
        await settingsService.UpdateSettings(settings);

        var saved = await unitOfWork.SettingsRepository.GetSettingsDtoAsync();
        Assert.Equal("http://text:8000", saved.TextSidecarUrl);
        Assert.True(saved.DictionaryAnalysisOnOpen);

        saved.TextSidecarUrl = "text:8000";
        var ex = await Assert.ThrowsAsync<KavitaException>(() => settingsService.UpdateSettings(saved));
        Assert.Equal("text-sidecar-url-invalid", ex.Message);
    }

    [Fact]
    public async Task UpdateSettings_SettingTheTextSidecarUrl_QueuesDetectionOnceAndWakesTheWorker()
    {
        var (unitOfWork, _, _) = await CreateDatabase();
        var settingsService = CreateSettingsService(unitOfWork);

        var settings = await unitOfWork.SettingsRepository.GetSettingsDtoAsync();
        settings.TextSidecarUrl = "http://text:8000";
        await settingsService.UpdateSettings(settings);

        Assert.Equal(1, QueuedDetections());
        _signal.Received(1).Wake();

        // A second address while the first run is still queued doesn't queue another
        settings.TextSidecarUrl = "http://text:9000";
        await settingsService.UpdateSettings(settings);

        Assert.Equal(1, QueuedDetections());
        _signal.Received(2).Wake();
    }

    [Theory]
    [InlineData("http://text:8000", "http://text:8000/")]
    [InlineData("http://text:8000", "")]
    [InlineData("", "")]
    public async Task UpdateSettings_TextSidecarUrlUnchangedOrCleared_QueuesNothing(string before, string after)
    {
        var (unitOfWork, context, _) = await CreateDatabase();
        context.ServerSetting.Single(s => s.Key == Kavita.Models.Entities.Enums.ServerSettingKey.TextSidecarUrl).Value = before;
        await context.SaveChangesAsync();
        var settingsService = CreateSettingsService(unitOfWork);

        var settings = await unitOfWork.SettingsRepository.GetSettingsDtoAsync();
        settings.TextSidecarUrl = after;
        settings.DictionaryAnalysisOnOpen = true;
        await settingsService.UpdateSettings(settings);

        Assert.Equal(0, QueuedDetections());
        _signal.DidNotReceive().Wake();
    }
}
