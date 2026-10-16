using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Agents.TrackerMaintenance;
using SpeakerPipeline.Notifications;

namespace SpeakerPipeline.Hosting.Functions.Functions;

/// <summary>
/// Reconciles Events.Category from submission status daily at 05:30 UTC — ahead
/// of the Monday 06:00 scoring run, so events that have already been submitted,
/// accepted, or turned down drop out of the scoring candidate pool.
/// Healthy runs with no changes are silent; deadline reminders and incidents
/// are evaluated independently.
/// </summary>
public sealed class TrackerMaintenanceTimerTrigger(
    PipelineRuns runs,
    ILogger<TrackerMaintenanceTimerTrigger> logger)
{
    [Function("TrackerMaintenanceTimerTrigger")]
    public async Task Run([TimerTrigger("0 30 5 * * *")] TimerInfo timer, CancellationToken ct)
    {
        logger.LogInformation("Tracker-maintenance run starting (scheduled). Next: {Next}", timer.ScheduleStatus?.Next);
        var result = await runs.TrackerAsync(ct);

        logger.LogInformation("Tracker-maintenance run complete. Updates: {Updates}, conflicts: {Conflicts}, urgent: {Urgent}",
            result.Updates.Count, result.ConflictChanges.Count, result.UrgentDeadlines.Count);
    }
}
