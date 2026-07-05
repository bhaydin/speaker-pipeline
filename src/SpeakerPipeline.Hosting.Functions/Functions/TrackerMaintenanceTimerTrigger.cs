using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SpeakerPipeline.Agents.TrackerMaintenance;
using SpeakerPipeline.Notifications;

namespace SpeakerPipeline.Hosting.Functions.Functions;

/// <summary>
/// Reconciles Events.Category from submission status daily at 05:30 UTC — ahead
/// of the Monday 06:00 scoring run, so events that have already been submitted,
/// accepted, or turned down drop out of the scoring candidate pool. Sends a
/// run-summary notification each run.
/// </summary>
public sealed class TrackerMaintenanceTimerTrigger(TrackerMaintenanceAgent agent, INotifier notifier, ILogger<TrackerMaintenanceTimerTrigger> logger)
{
    [Function("TrackerMaintenanceTimerTrigger")]
    public async Task Run([TimerTrigger("0 30 5 * * *")] TimerInfo timer, CancellationToken ct)
    {
        logger.LogInformation("Tracker-maintenance run starting (scheduled). Next: {Next}", timer.ScheduleStatus?.Next);
        var updates = await agent.RunAsync(ct);
        await TrackerNotification.SendAsync(notifier, updates, ct);
        logger.LogInformation("Tracker-maintenance run complete. Updates: {Count}", updates.Count);
    }
}
