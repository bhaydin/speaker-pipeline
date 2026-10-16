using SpeakerPipeline.Agents.TrackerMaintenance;
using SpeakerPipeline.Notifications;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Hosting.Functions.Functions;

/// <summary>
/// Bridges a tracker-maintenance run's updates to the Notifier — maps the agent's
/// <see cref="TrackerUpdate"/>s to generic digest items so the Notifications
/// project stays decoupled from the tracker agent. The shared run wrapper calls
/// this only for meaningful changes and supplies the latest event display facts.
/// </summary>
internal static class TrackerNotification
{
    public static async Task SendAsync(
        INotifier notifier,
        IReadOnlyList<TrackerUpdate> updates,
        IReadOnlyList<ConflictChange> conflicts,
        CancellationToken ct,
        IReadOnlyDictionary<string, EventRecord>? events = null,
        string? deliveryKey = null,
        IReadOnlyDictionary<string, OpportunityRecord>? opportunities = null)
    {
        NotificationItem Item(string slug, string detail)
        {
            var ev = events?.GetValueOrDefault(slug);
            var opportunity = opportunities?.GetValueOrDefault(slug);
            return new NotificationItem { Id = slug, EventSlug = slug, Name = ev?.Name ?? slug,
                Url = ev is null ? null : NotificationLinks.ForEvent(ev), Detail = detail,
                ExpectedETag = opportunity?.ETag, DecisionId = opportunity?.LatestDecisionId,
                Actions = ev?.Category is not (EventCategory.Submitted or EventCategory.Accepted or EventCategory.Delivered or EventCategory.Skip or EventCategory.Pass) };
        }
        var groups = new List<NotificationGroup>();
        if (updates.Count > 0) groups.Add(new("Tracker changes", updates.Select(u => Item(u.EventSlug, $"{u.From} → {u.To}")).ToArray()));
        if (conflicts.Count > 0) groups.Add(new("Schedule changes", conflicts.Select(c => Item(c.EventSlug, DescribeConflict(c))).ToArray()));
        if (groups.Count == 0) return;
        await notifier.NotifyAsync(new Notification { Subject = "Speaker pipeline — tracker", HtmlBody = "", Groups = groups, DeliveryKey = deliveryKey }, ct);
    }

    private static string DescribeConflict(ConflictChange c)
    {
        var flags = new List<string>();
        if (c.Family)
        {
            flags.Add("family blackout overlap");
        }
        if (c.Prep)
        {
            flags.Add("prep congestion");
        }
        return flags.Count == 0 ? "conflicts cleared" : string.Join("; ", flags);
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
