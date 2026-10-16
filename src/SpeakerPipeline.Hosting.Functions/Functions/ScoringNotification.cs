using SpeakerPipeline.Agents.Scoring;
using SpeakerPipeline.Notifications;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Hosting.Functions.Functions;

/// <summary>
/// Bridges a scoring run's verdicts to the Notifier — maps the agent's
/// <see cref="ScoredVerdict"/>s to the decoupled digest view so the Notifications
/// project stays independent of the scoring agent, and sends the run summary.
/// </summary>
internal static class ScoringNotification
{
    public static async Task SendSnapshotAsync(INotifier notifier, IReadOnlyList<OpportunityRecord> snapshot,
        IReadOnlyList<ScoredVerdict> decisions, DateTimeOffset now, CancellationToken ct)
    {
        var changes = decisions.Where(d => d.Change == VerdictChange.Changed).ToDictionary(d => d.Decision.EventSlug,
            d => new OpportunityVerdictChange(d.PriorCategory, d.Decision.Recommendation, d.Decision.ChangeReason));
        await notifier.NotifyAsync(OpportunitySnapshotDigest.Build(snapshot, changes, now), ct);
    }

    public static async Task SendAsync(INotifier notifier, IReadOnlyList<ScoredVerdict> verdicts, CancellationToken ct)
    {
        var views = verdicts
            .Select(v => new ScoredVerdictView(
                v.Decision.EventSlug, v.Decision.Recommendation, v.Decision.FitScore, v.Decision.EffortScore,
                v.PriorCategory, v.Change))
            .ToArray();

        await notifier.NotifyAsync(ScoringDigest.Build(views), ct);
    }
}
