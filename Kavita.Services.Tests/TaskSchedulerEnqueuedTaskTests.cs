using Hangfire;
using Hangfire.InMemory;
using Kavita.API.Services;
using Kavita.Services.Tests.Helpers;
using NSubstitute;

namespace Kavita.Services.Tests;

[Collection(HangfireStorageCollection.Name)]
public class TaskSchedulerEnqueuedTaskTests
{
    public TaskSchedulerEnqueuedTaskTests()
    {
        JobStorage.Current = new InMemoryStorage();
    }

    [Fact]
    public void HasAlreadyEnqueuedTask_SkipsAnEnqueuedJobThatCannotLoad()
    {
        // A job queued on a substitute has the proxy's type, which Hangfire can't load back, so the monitoring API lists it with no Job
        var cacheService = Substitute.For<ICacheService>();
        var unloadable = BackgroundJob.Enqueue(() => cacheService.CleanupChapters(new[] { 1 }));
        Assert.Null(JobStorage.Current.GetMonitoringApi().JobDetails(unloadable).Job);
        BackgroundJob.Enqueue<IOcrService>(s => s.DetectNewFilesAsync());

        Assert.True(TaskScheduler.HasAlreadyEnqueuedTask(nameof(IOcrService), nameof(IOcrService.DetectNewFilesAsync), []));
        Assert.False(TaskScheduler.HasAlreadyEnqueuedTask(nameof(ITokenizeService), nameof(ITokenizeService.DetectNewFilesAsync), []));
    }
}
