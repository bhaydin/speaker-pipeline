using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Agents.Scoring;
using SpeakerPipeline.Notifications;

namespace SpeakerPipeline.Hosting.Functions.Functions;

/// <summary>
/// Runs the scoring agent on a schedule. Monday 06:00 UTC by default —
/// the digest goes out Monday morning Central Time, so scoring needs to be
/// fresh by then. Sends a run-summary notification unless the run changed
/// nothing and Notifications:SuppressEmptyScheduledRuns is set.
/// </summary>
public sealed class ScoringAgentTimerTrigger(
    ScoringAgent agent,
    INotifier notifier,
    IOptions<NotificationOptions> notificationOptions,
    ILogger<ScoringAgentTimerTrigger> logger)
{
    [Function("ScoringAgentTimerTrigger")]
    public async Task Run([TimerTrigger("0 0 6 * * MON")] TimerInfo timer, CancellationToken ct)
    {
        logger.LogInformation("Scoring agent run starting (scheduled). Next: {Next}", timer.ScheduleStatus?.Next);
        var verdicts = await agent.RunAsync(ct);

        if (NotificationPolicy.ShouldNotify(verdicts.Count, isScheduled: true, notificationOptions.Value.SuppressEmptyScheduledRuns))
        {
            await ScoringNotification.SendAsync(notifier, verdicts, ct);
        }

        logger.LogInformation("Scoring agent run complete. Decisions: {Count}", verdicts.Count);
    }
}
