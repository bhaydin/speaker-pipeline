using SpeakerPipeline.Agents.Discovery;
using SpeakerPipeline.Notifications;

namespace SpeakerPipeline.Hosting.Functions.Functions;

/// <summary>
/// Bridges a discovery run's results to the Notifier — maps the agent's
/// <see cref="DiscoveryResult"/>s to generic digest items so the Notifications
/// project stays decoupled from the discovery agent, and sends a summary only
/// when something actually changed.
/// </summary>
internal static class DiscoveryNotification
{
    public static async Task SendAsync(INotifier notifier, IReadOnlyList<DiscoveryResult> results, CancellationToken ct)
    {
        var items = results
            .Select(r => new DigestItem(r.EventName, r.IsNew, r.ChangeSummary))
            .ToArray();

        var notification = DiscoveryDigest.Build(items);
        if (notification is not null)
        {
            await notifier.NotifyAsync(notification, ct);
        }
    }
}
