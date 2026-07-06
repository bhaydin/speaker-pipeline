using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Agents.TrackerMaintenance;
using SpeakerPipeline.Notifications;

namespace SpeakerPipeline.Hosting.Functions.Functions;

/// <summary>
/// Reconciles Events.Category from submission status daily at 05:30 UTC — ahead
/// of the Monday 06:00 scoring run, so events that have already been submitted,
/// accepted, or turned down drop out of the scoring candidate pool. Sends a
/// run summary unless the run changed nothing and
/// Notifications:SuppressEmptyScheduledRuns is set.
/// </summary>
public sealed class TrackerMaintenanceTimerTrigger(
    TrackerMaintenanceAgent agent,
    INotifier notifier,
    IOptions<NotificationOptions> notificationOptions,
    ILogger<TrackerMaintenanceTimerTrigger> logger)
{
    [Function("TrackerMaintenanceTimerTrigger")]
    public async Task Run([TimerTrigger("0 30 5 * * *")] TimerInfo timer, CancellationToken ct)
    {
        logger.LogInformation("Tracker-maintenance run starting (scheduled). Next: {Next}", timer.ScheduleStatus?.Next);
        var result = await agent.RunAsync(ct);

        // Urgent deadline alerts always go out (deduped); the category-update digest honors suppression.
        await TrackerNotification.SendUrgentDeadlinesAsync(notifier, result.UrgentDeadlines, ct);

        if (NotificationPolicy.ShouldNotify(result.Updates.Count, isScheduled: true, notificationOptions.Value.SuppressEmptyScheduledRuns))
        {
            await TrackerNotification.SendAsync(notifier, result.Updates, ct);
        }

        logger.LogInformation("Tracker-maintenance run complete. Updates: {Updates}, urgent: {Urgent}",
            result.Updates.Count, result.UrgentDeadlines.Count);
    }
}
