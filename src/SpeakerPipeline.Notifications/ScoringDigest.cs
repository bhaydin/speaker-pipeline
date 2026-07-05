using System.Net;
using System.Text;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Notifications;

/// <summary>
/// Formats a scoring run into a notification. Unlike the discovery digest, this
/// <b>always</b> returns a notification — a scoring run is a heartbeat worth
/// confirming even when nothing was due, so "I ran it, did it work?" has an
/// answer in the inbox.
/// </summary>
public static class ScoringDigest
{
    public static Notification Build(IReadOnlyList<ScoringDecision> decisions)
    {
        var submitNow = decisions.Count(d => d.Recommendation == Recommendation.SubmitNow);

        var body = new StringBuilder();
        body.Append("<h2>Scoring run</h2>");

        if (decisions.Count == 0)
        {
            body.Append("<p>No events were due for scoring.</p>");
        }
        else
        {
            var counts = decisions
                .GroupBy(d => d.Recommendation)
                .OrderBy(g => g.Key)
                .Select(g => $"{g.Count()} {g.Key}");
            body.Append($"<p><strong>{decisions.Count}</strong> scored — {string.Join(", ", counts)}.</p>");

            var highlights = decisions
                .Where(d => d.Recommendation is Recommendation.SubmitNow or Recommendation.Outreach)
                .OrderByDescending(d => d.FitScore)
                .ToList();

            if (highlights.Count > 0)
            {
                body.Append("<ul>");
                foreach (var d in highlights)
                {
                    body.Append($"<li><strong>{Encode(d.EventSlug)}</strong> — {d.Recommendation}, fit {d.FitScore}/10, effort {d.EffortScore}/10</li>");
                }
                body.Append("</ul>");
            }
        }

        var tail = decisions.Count == 0
            ? "nothing due"
            : $"{decisions.Count} scored" + (submitNow > 0 ? $", {submitNow} SubmitNow" : string.Empty);

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
