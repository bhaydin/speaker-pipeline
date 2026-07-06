using SpeakerPipeline.Agents.Scoring;
using SpeakerPipeline.Notifications;

namespace SpeakerPipeline.Hosting.Functions.Functions;

/// <summary>
/// Bridges a scoring run's verdicts to the Notifier — maps the agent's
/// <see cref="ScoredVerdict"/>s to the decoupled digest view so the Notifications
/// project stays independent of the scoring agent, and sends the run summary.
/// </summary>
internal static class ScoringNotification
{
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
