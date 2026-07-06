using SpeakerPipeline.Core;
using SpeakerPipeline.Notifications;

namespace SpeakerPipeline.Notifications.Tests;

public class ScoringDigestTests
{
    private static ScoredVerdictView Verdict(
        string slug, Recommendation rec, int fit,
        VerdictChange change = VerdictChange.New, EventCategory prior = EventCategory.Monitor) =>
        new(slug, rec, fit, EffortScore: 3, PriorCategory: prior, Change: change);

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
        var verdicts = new[]
        {
            Verdict("northwoods", Recommendation.SubmitNow, 9),
            Verdict("driftless", Recommendation.Outreach, 6),
            Verdict("far-away", Recommendation.Monitor, 4),
        };

        var n = ScoringDigest.Build(verdicts);

        Assert.Contains("3 scored", n.Subject);
        Assert.Contains("1 SubmitNow", n.Subject);
        Assert.Contains("northwoods", n.HtmlBody);   // actionable items are listed
        Assert.Contains("driftless", n.HtmlBody);
        Assert.Contains("fit 9/10", n.HtmlBody);
    }

    [Fact]
    public void Build_headlines_verdict_flips()
    {
        var verdicts = new[]
        {
            Verdict("flipped", Recommendation.Monitor, 8, VerdictChange.Changed, prior: EventCategory.SubmitNow),
            Verdict("steady", Recommendation.SubmitNow, 9, VerdictChange.Unchanged, prior: EventCategory.SubmitNow),
        };

        var n = ScoringDigest.Build(verdicts);

        Assert.Contains("1 flipped", n.Subject);
        Assert.Contains("Verdict flips (1)", n.HtmlBody);
        Assert.Contains("SubmitNow → Monitor", n.HtmlBody);   // shows the direction of the flip
    }

    [Fact]
    public void Build_orders_highlights_by_fit_descending()
    {
        var verdicts = new[]
        {
            Verdict("low-fit", Recommendation.SubmitNow, 5),
            Verdict("high-fit", Recommendation.SubmitNow, 10),
        };

        var body = ScoringDigest.Build(verdicts).HtmlBody;

        Assert.True(body.IndexOf("high-fit", StringComparison.Ordinal) < body.IndexOf("low-fit", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_sets_no_dedupe_key_so_every_run_sends()
    {
        var n = ScoringDigest.Build([Verdict("x", Recommendation.Monitor, 5)]);
        Assert.Null(n.DedupeKey);
    }
}
