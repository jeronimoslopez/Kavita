using Kavita.API.Services;
using Kavita.API.Services.Scanner;
using Kavita.Common;
using Kavita.Database.Tests;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit.Abstractions;

namespace Kavita.Services.Tests.Dictionary;

public class TextSidecarSettingsTests(ITestOutputHelper outputHelper) : AbstractDbTest(outputHelper)
{
    [Fact]
    public async Task UpdateSettings_StoresTheTextSidecarUrlAndRejectsBadValues()
    {
        var (unitOfWork, _, _) = await CreateDatabase();
        var settingsService = new SettingsService(unitOfWork, Substitute.For<IDirectoryService>(), Substitute.For<ILibraryWatcher>(),
            Substitute.For<ITaskScheduler>(), Substitute.For<ILogger<SettingsService>>(), Substitute.For<IOidcService>(),
            Substitute.For<ILoggingService>());

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
}
