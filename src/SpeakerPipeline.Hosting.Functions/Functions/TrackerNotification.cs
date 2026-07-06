using SpeakerPipeline.Agents.TrackerMaintenance;
using SpeakerPipeline.Notifications;

namespace SpeakerPipeline.Hosting.Functions.Functions;

/// <summary>
/// Bridges a tracker-maintenance run's updates to the Notifier — maps the agent's
/// <see cref="TrackerUpdate"/>s to generic digest items so the Notifications
/// project stays decoupled from the tracker agent, and sends a run summary
/// (including a "no changes" heartbeat).
/// </summary>
internal static class TrackerNotification
{
    public static async Task SendAsync(INotifier notifier, IReadOnlyList<TrackerUpdate> updates, CancellationToken ct)
    {
        var items = updates
            .Select(u => new DigestItem(u.EventSlug, IsNew: false, $"{u.From} → {u.To}"))
            .ToArray();

        await notifier.NotifyAsync(TrackerDigest.Build(items), ct);
    }

    /// <summary>
    /// Fires an urgent, dedupe-keyed notification per imminent deadline. Sent
    /// regardless of the empty-run suppression policy — an urgent alert is the
    /// point of the daily sweep — and deduped so it doesn't re-nag each day.
    /// </summary>
    public static async Task SendUrgentDeadlinesAsync(INotifier notifier, IReadOnlyList<UrgentDeadline> urgent, CancellationToken ct)
    {
        foreach (var u in urgent)
        {
            await notifier.NotifyAsync(UrgentDeadlineNotice.Build(u.EventSlug, u.EventName, u.Deadline, u.DaysRemaining), ct);
        }
    }
}
