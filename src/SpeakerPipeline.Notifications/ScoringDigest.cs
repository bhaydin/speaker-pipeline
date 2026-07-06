using System.Net;
using System.Text;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Notifications;

/// <summary>
/// Formats a scoring run into a notification. Unlike the discovery digest, this
/// <b>always</b> returns a notification — a scoring run is a heartbeat worth
/// confirming even when nothing was due, so "I ran it, did it work?" has an
/// answer in the inbox. Verdict <em>flips</em> (a decision that moved an event to
/// a different category) lead the digest — they're the signal; unchanged
/// re-scores are noise.
/// </summary>
public static class ScoringDigest
{
    public static Notification Build(IReadOnlyList<ScoredVerdictView> verdicts)
    {
        var submitNow = verdicts.Count(v => v.Recommendation == Recommendation.SubmitNow);
        var flips = verdicts.Where(v => v.Change == VerdictChange.Changed)
            .OrderByDescending(v => v.FitScore)
            .ToList();

        var body = new StringBuilder();
        body.Append("<h2>Scoring run</h2>");

        if (verdicts.Count == 0)
        {
            body.Append("<p>No events were due for scoring.</p>");
        }
        else
        {
            var counts = verdicts
                .GroupBy(v => v.Recommendation)
                .OrderBy(g => g.Key)
                .Select(g => $"{g.Count()} {g.Key}");
            body.Append($"<p><strong>{verdicts.Count}</strong> scored — {string.Join(", ", counts)}.</p>");

            if (flips.Count > 0)
            {
                body.Append($"<h3>Verdict flips ({flips.Count})</h3><ul>");
                foreach (var v in flips)
                {
                    body.Append($"<li><strong>{Encode(v.EventSlug)}</strong> — {v.PriorCategory} → {v.Recommendation}, fit {v.FitScore}/10</li>");
                }
                body.Append("</ul>");
            }

            var highlights = verdicts
                .Where(v => v.Change != VerdictChange.Changed
                            && v.Recommendation is Recommendation.SubmitNow or Recommendation.Outreach)
                .OrderByDescending(v => v.FitScore)
                .ToList();

            if (highlights.Count > 0)
            {
                body.Append("<h3>Actionable</h3><ul>");
                foreach (var v in highlights)
                {
                    body.Append($"<li><strong>{Encode(v.EventSlug)}</strong> — {v.Recommendation}, fit {v.FitScore}/10, effort {v.EffortScore}/10</li>");
                }
                body.Append("</ul>");
            }
        }

        var tail = verdicts.Count == 0
            ? "nothing due"
            : $"{verdicts.Count} scored"
              + (flips.Count > 0 ? $", {flips.Count} flipped" : string.Empty)
              + (submitNow > 0 ? $", {submitNow} SubmitNow" : string.Empty);

        return new Notification
        {
            Subject = $"Speaker pipeline — scoring: {tail}",
            HtmlBody = body.ToString(),
            Urgency = NotificationUrgency.Digest,
            // No dedupe key: each run's outcome is worth its own confirmation.
        };
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}

/// <summary>
/// A decoupled view of one scored verdict for the digest — mirrors the scoring
/// agent's result without the Notifications project referencing the agent.
/// </summary>
public sealed record ScoredVerdictView(
    string EventSlug,
    Recommendation Recommendation,
    int FitScore,
    int EffortScore,
    EventCategory PriorCategory,
    VerdictChange Change);
