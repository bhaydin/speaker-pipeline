using SpeakerPipeline.Agents.Discovery;
using SpeakerPipeline.Notifications;

namespace SpeakerPipeline.Hosting.Functions.Functions;

/// <summary>
/// Bridges a discovery run's report to the Notifier — maps the agent's results,
/// quarantine list, and funnel to generic digest shapes so the Notifications
/// project stays decoupled from the discovery agent, and sends a run summary
/// (including a "nothing new" heartbeat).
/// </summary>
internal static class DiscoveryNotification
{
    public static async Task SendAsync(INotifier notifier, DiscoveryRunReport report, CancellationToken ct)
    {
        var items = report.Changed
            .Select(r => new DigestItem(r.EventName, r.IsNew, r.ChangeSummary))
            .ToArray();

        var quarantined = report.Quarantined
            .Select(r => new DigestItem(r.EventName, r.IsNew, r.ChangeSummary))
            .ToArray();

        var f = report.Funnel;
        var funnel = new DiscoveryFunnelView(
            f.Targets, f.Extracted, f.PassedFloor, f.New, f.Updated, f.Quarantined,
            f.Dropped, f.CandidatesBySource, f.InputTokens, f.OutputTokens, f.SearchQueries);

        await notifier.NotifyAsync(DiscoveryDigest.Build(items, quarantined, funnel), ct);
    }
}
