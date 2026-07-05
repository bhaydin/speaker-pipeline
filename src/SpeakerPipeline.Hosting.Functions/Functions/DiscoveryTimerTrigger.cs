using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Agents.Discovery;
using SpeakerPipeline.Notifications;

namespace SpeakerPipeline.Hosting.Functions.Functions;

/// <summary>
/// Daily light scan at 05:00 UTC — ahead of tracker-maintenance (05:30) and the
/// Monday scoring run (06:00), so newly discovered events are reconciled and
/// scored in the same cascade. Sends a run summary unless the run changed nothing
/// and Notifications:SuppressEmptyScheduledRuns is set.
/// </summary>
public sealed class DiscoveryTimerTrigger(
    DiscoveryAgent agent,
    INotifier notifier,
    IOptions<NotificationOptions> notificationOptions,
    ILogger<DiscoveryTimerTrigger> logger)
{
    [Function("DiscoveryTimerTrigger")]
    public async Task Run([TimerTrigger("0 0 5 * * *")] TimerInfo timer, CancellationToken ct)
    {
        logger.LogInformation("Discovery run starting (scheduled). Next: {Next}", timer.ScheduleStatus?.Next);
        var results = await agent.RunAsync(ct);
        logger.LogInformation("Discovery run complete. Events changed: {Count}", results.Count);

        if (NotificationPolicy.ShouldNotify(results.Count, isScheduled: true, notificationOptions.Value.SuppressEmptyScheduledRuns))
        {
            await DiscoveryNotification.SendAsync(notifier, results, ct);
        }
    }
}
