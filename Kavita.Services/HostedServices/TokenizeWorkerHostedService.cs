using System.Threading;
using System.Threading.Tasks;
using Kavita.API.Services;
using Microsoft.Extensions.Hosting;

namespace Kavita.Services.HostedServices;

/// <summary>
/// Tokenizes pending reading.db units one page at a time with the text sidecar. Like the OCR worker it runs outside
/// Hangfire, and its queue is the pending unit rows
/// </summary>
public class TokenizeWorkerHostedService : BackgroundService, ITokenizeWorkerSignal
{
    public void Wake()
    {
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}
