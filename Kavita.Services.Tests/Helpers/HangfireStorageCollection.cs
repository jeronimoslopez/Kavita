namespace Kavita.Services.Tests.Helpers;

/// <summary>
/// For tests that read jobs back from <see cref="Hangfire.JobStorage.Current"/>, directly or through the code under test.
/// Other classes swap it while they run in parallel, and fill it with jobs queued on substitutes that Hangfire can't load
/// back, so these run on their own
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class HangfireStorageCollection
{
    public const string Name = "Hangfire storage";
}
