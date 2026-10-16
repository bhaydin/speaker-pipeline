using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Agents.Discovery;
using SpeakerPipeline.Notifications;

namespace SpeakerPipeline.Hosting.Functions.Functions;

/// <summary>
/// Daily light scan at 05:00 UTC — ahead of tracker-maintenance (05:30) and the
/// Monday scoring run (06:00), so newly discovered events are reconciled and
/// scored in the same cascade. Healthy runs with no changes are silent;
/// incidents and recovery are reported independently.
/// </summary>
public sealed class DiscoveryTimerTrigger(
    PipelineRuns runs,
    ILogger<DiscoveryTimerTrigger> logger)
{
    [Function("DiscoveryTimerTrigger")]
    public async Task Run([TimerTrigger("0 0 5 * * *")] TimerInfo timer, CancellationToken ct)
    {
        logger.LogInformation("Discovery run starting (scheduled). Next: {Next}", timer.ScheduleStatus?.Next);
        var report = await runs.DiscoveryAsync(ct);
        logger.LogInformation("Discovery run complete. Changed: {Changed}, quarantined: {Quarantined}",
            report.Changed.Count, report.Quarantined.Count);

    }
}
