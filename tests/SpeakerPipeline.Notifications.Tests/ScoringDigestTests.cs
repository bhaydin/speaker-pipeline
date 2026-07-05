using SpeakerPipeline.Core;
using SpeakerPipeline.Notifications;

namespace SpeakerPipeline.Notifications.Tests;

public class ScoringDigestTests
{
    private static ScoringDecision Decision(string slug, Recommendation rec, int fit) => new()
    {
        EventSlug = slug,
        Recommendation = rec,
        Rationale = "because",
        FitScore = fit,
        EffortScore = 3,
        ConfidenceScore = 7,
    };

    [Fact]
    public void Build_on_empty_run_still_returns_a_heartbeat_notification()
    {
        var n = ScoringDigest.Build([]);

        Assert.Contains("nothing due", n.Subject);
        Assert.Contains("No events were due", n.HtmlBody);
    }

    [Fact]
    public void Build_summarizes_counts_and_highlights_actionable()
    {
        var decisions = new[]
        {
            Decision("northwoods", Recommendation.SubmitNow, 9),
            Decision("driftless", Recommendation.Outreach, 6),
            Decision("far-away", Recommendation.Monitor, 4),
        };

        var n = ScoringDigest.Build(decisions);

        Assert.Contains("3 scored", n.Subject);
        Assert.Contains("1 SubmitNow", n.Subject);
        Assert.Contains("northwoods", n.HtmlBody);   // actionable items are listed
        Assert.Contains("driftless", n.HtmlBody);
        Assert.Contains("fit 9/10", n.HtmlBody);
    }

    [Fact]
    public void Build_orders_highlights_by_fit_descending()
    {
        var decisions = new[]
        {
            Decision("low-fit", Recommendation.SubmitNow, 5),
            Decision("high-fit", Recommendation.SubmitNow, 10),
        };

        var body = ScoringDigest.Build(decisions).HtmlBody;

        Assert.True(body.IndexOf("high-fit", StringComparison.Ordinal) < body.IndexOf("low-fit", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_sets_no_dedupe_key_so_every_run_sends()
    {
        var n = ScoringDigest.Build([Decision("x", Recommendation.Monitor, 5)]);
        Assert.Null(n.DedupeKey);
    }
}
