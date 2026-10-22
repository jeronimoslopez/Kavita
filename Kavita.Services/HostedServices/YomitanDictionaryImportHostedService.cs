using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kavita.Services.Yomitan;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kavita.Services.HostedServices;

/// <summary>
/// Imports the Yomitan dictionaries bundled next to the server binaries. Runs in the background so startup isn't held up,
/// lookups keep working against whatever is already imported.
/// </summary>
public class YomitanDictionaryImportHostedService(YomitanDictionaryImporter importer, YomitanImportProgress progress,
    ILogger<YomitanDictionaryImportHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the host finish starting before the import takes a core
        await Task.Yield();

        var directory = YomitanDictionaryDatabase.BundledDictionaryDirectory;
        if (!Directory.Exists(directory))
        {
            logger.LogInformation("[Yomitan] No bundled dictionaries at {Directory}", directory);
            progress.Start(0);
            progress.Finish();
            return;
        }

        // Smallest first so the frequency and kanji dictionaries are usable while the large ones import
        var zips = new DirectoryInfo(directory).GetFiles("*.zip")
            .OrderBy(f => f.Length)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .ToList();

        progress.Start(zips.Count);
        try
        {
            foreach (var zip in zips)
            {
                stoppingToken.ThrowIfCancellationRequested();
                progress.BeginDictionary(zip.Name);

                var result = await importer.ImportIfNeededAsync(zip.FullName, zip.Name, progress, stoppingToken);
                switch (result.Outcome)
                {
                    case YomitanImportOutcome.Skipped:
                        logger.LogDebug("[Yomitan] {Title} ({Revision}) is already imported", result.Title, result.Revision);
                        break;
                    case YomitanImportOutcome.Imported:
                        logger.LogInformation("[Yomitan] Imported {Title} ({Revision}) from {File} in {Elapsed:0.0}s",
                            result.Title, result.Revision, zip.Name, result.Elapsed.TotalSeconds);
                        break;
                    case YomitanImportOutcome.Failed:
                        logger.LogError("[Yomitan] Import of {File} failed after {Elapsed:0.0}s: {Error}",
                            zip.Name, result.Elapsed.TotalSeconds, result.Error);
                        break;
                }
                progress.EndDictionary(result.Outcome == YomitanImportOutcome.Failed ? $"{zip.Name}: {result.Error}" : null);
            }
            progress.Finish();
        }
        catch (OperationCanceledException ex) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation(ex, "[Yomitan] Dictionary import stopped by shutdown, it resumes on the next start");
            progress.Finish("Stopped by shutdown");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Yomitan] Dictionary import failed");
            progress.Finish(ex.Message);
        }
    }
}
