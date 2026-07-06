using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using SpeakerPipeline.Agents.Discovery;
using SpeakerPipeline.Notifications;

namespace SpeakerPipeline.Hosting.Functions.Functions;

/// <summary>
/// Manual ad-hoc trigger for the discovery agent. Auth required (function key).
/// Fetches the watchlist, extracts events, reconciles them into the tracker, and
/// emails a summary of what changed.
/// </summary>
public sealed class DiscoveryHttpTrigger(DiscoveryAgent agent, INotifier notifier, ILogger<DiscoveryHttpTrigger> logger)
{
    [Function("DiscoveryHttpTrigger")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "discovery/run")] HttpRequestData request,
        CancellationToken ct)
    {
        logger.LogInformation("Discovery run starting (manual).");
        var report = await agent.RunAsync(ct);
        await DiscoveryNotification.SendAsync(notifier, report, ct);

        var response = request.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(
            new { changed = report.Changed.Count, quarantined = report.Quarantined.Count, report.Funnel, report.Changed, report.Quarantined },
            ct);
        return response;
    }
}
