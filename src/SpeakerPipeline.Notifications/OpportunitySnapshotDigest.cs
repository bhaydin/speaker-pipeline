using SpeakerPipeline.Core;

namespace SpeakerPipeline.Notifications;

public sealed record OpportunityVerdictChange(EventCategory PriorCategory, Recommendation Recommendation, string? Reason);

/// <summary>A complete recommendation snapshot; user placement is displayed without changing it.</summary>
public static class OpportunitySnapshotDigest
{
    public static Notification Build(IReadOnlyList<OpportunityRecord> snapshot,
        IReadOnlyDictionary<string, OpportunityVerdictChange> changes, DateTimeOffset now)
    {
        var eligible = snapshot.Select(o => (Opportunity: o, Verdict: OpportunityNotificationPolicy.SnapshotVerdict(o, now))).ToArray();
        var groups = new List<NotificationGroup>();
        foreach (var verdict in new[] { Recommendation.SubmitNow, Recommendation.Outreach })
        {
            var items = eligible.Where(o => o.Verdict == verdict)
                .OrderBy(o => o.Opportunity.Event.CfpDeadline ?? DateTimeOffset.MaxValue)
                .ThenBy(o => o.Opportunity.Event.Name, StringComparer.Ordinal)
                .Select(o => Item(o.Opportunity, verdict, changes.GetValueOrDefault(o.Opportunity.Slug), now)).ToArray();
            if (items.Length > 0) groups.Add(new(verdict.ToString(), items));
        }
        var monitor = eligible.Count(o => o.Verdict == Recommendation.Monitor);
        return new Notification
        {
            Subject = $"Weekly opportunities — {groups.FirstOrDefault(g => g.Title == nameof(Recommendation.SubmitNow))?.Items.Count ?? 0} SubmitNow · {groups.FirstOrDefault(g => g.Title == nameof(Recommendation.Outreach))?.Items.Count ?? 0} Outreach · {monitor} Monitor",
            HtmlBody = groups.Count == 0 ? "<p>No actionable recommendations this week.</p>" : "",
            Groups = groups,
            DeliveryKey = $"snapshot-{PipelineHealth.WeekKey(now)}-" + OperationState.Key(System.Text.Json.JsonSerializer.Serialize(
                new { Monitor = monitor, Groups = groups.Select(g => new { g.Title, Items = g.Items.Select(i => new { i.Id, i.Name, i.Url, i.Detail, i.Actions }) }) })),
        };
    }

    private static NotificationItem Item(OpportunityRecord opportunity, Recommendation verdict, OpportunityVerdictChange? changed, DateTimeOffset now)
    {
        var ev = opportunity.Event;
        var decision = opportunity.LatestDecision;
        var deadline = ev.CfpDeadline is { } d
            ? $"CFP closes {d:MMM d, yyyy} · {(int)Math.Ceiling((d - now).TotalDays)} days remaining" + (d >= now && d <= now.AddDays(14) ? " · DUE WITHIN 14 DAYS" : "")
            : "CFP deadline unknown";
        var dates = ev.EventDateStart is { } start ? $"Event {start:MMM d, yyyy}" + (ev.EventDateEnd is { } end && end.Date != start.Date ? $"–{end:MMM d, yyyy}" : "") : "Event dates unknown";
        var location = ev.Format == EventFormat.Virtual ? "Virtual" : ev.Location ?? "Location unknown";
        var talk = decision?.MatchedTalks.Count > 0 ? string.Join("; ", decision.MatchedTalks.Select(t => t.Title)) : "No matched talk recorded";
        var rationale = new[] { decision?.FitRationale, decision?.Rationale, ev.NextAction }.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "Awaiting scoring rationale.";
        var placement = $"Recommendation: {verdict}" + (opportunity.UserStatus is { } pinned ? $" · Your status: {pinned}" : "");
        var detail = $"{placement}\n{deadline}\n{dates} · {location}\nTalk: {talk}\nWhy: {rationale}";
        if (changed is not null && changed.Recommendation == verdict)
            detail += $"\nVerdict changed: {changed.PriorCategory} → {changed.Recommendation}." + (!string.IsNullOrWhiteSpace(changed.Reason) ? $" {changed.Reason}" : "");
        return new NotificationItem { Id = ev.Slug, EventSlug = ev.Slug, Name = ev.Name, Url = NotificationLinks.ForEvent(ev), Detail = detail, Actions = true,
            ExpectedETag = opportunity.ETag, DecisionId = opportunity.LatestDecisionId };
    }
}
