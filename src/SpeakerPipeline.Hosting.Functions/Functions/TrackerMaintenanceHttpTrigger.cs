using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using SpeakerPipeline.Agents.TrackerMaintenance;
using SpeakerPipeline.Notifications;

namespace SpeakerPipeline.Hosting.Functions.Functions;

/// <summary>
/// Manual ad-hoc trigger for the tracker-maintenance agent. Auth required
/// (function key). Reconciles Events.Category from submission status on demand,
/// and sends a run-summary notification.
/// </summary>
public sealed class TrackerMaintenanceHttpTrigger(TrackerMaintenanceAgent agent, INotifier notifier, ILogger<TrackerMaintenanceHttpTrigger> logger)
{
    [Function("TrackerMaintenanceHttpTrigger")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "tracker/run")] HttpRequestData request,
        CancellationToken ct)
    {
        logger.LogInformation("Tracker-maintenance run starting (manual).");
        var result = await agent.RunAsync(ct);
        await TrackerNotification.SendUrgentDeadlinesAsync(notifier, result.UrgentDeadlines, ct);
        await TrackerNotification.SendAsync(notifier, result.Updates, result.ConflictChanges, ct);

        var response = request.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(
            new { count = result.Updates.Count, result.Updates, result.ConflictChanges, result.UrgentDeadlines }, ct);
        return response;
    }
}
