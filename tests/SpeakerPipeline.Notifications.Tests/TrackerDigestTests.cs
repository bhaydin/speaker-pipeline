using SpeakerPipeline.Notifications;

namespace SpeakerPipeline.Notifications.Tests;

public class TrackerDigestTests
{
    [Fact]
    public void Build_on_empty_run_returns_a_no_changes_heartbeat()
    {
        var n = TrackerDigest.Build([]);

        Assert.Contains("no changes", n.Subject);
        Assert.Contains("No category changes", n.HtmlBody);
    }

    [Fact]
    public void Build_lists_the_transitions_and_counts_them()
    {
        var items = new[]
        {
            new DigestItem("northwoods", IsNew: false, "SubmitNow → Submitted"),
            new DigestItem("great-lakes", IsNew: false, "Submitted → Accepted"),
        };

        var n = TrackerDigest.Build(items);

        Assert.Contains("2 changed", n.Subject);
        Assert.Contains("northwoods", n.HtmlBody);
        Assert.Contains("SubmitNow → Submitted", n.HtmlBody);
        Assert.Contains("Submitted → Accepted", n.HtmlBody);
    }

    [Fact]
    public void Build_html_encodes_titles()
    {
        var n = TrackerDigest.Build([new DigestItem("dev & ops", IsNew: false, "A → B")]);

        Assert.Contains("dev &amp; ops", n.HtmlBody);
    }

    [Fact]
    public void Build_lists_conflict_flags_and_counts_them_in_subject()
    {
        var conflicts = new[]
        {
            new DigestItem("keweenaw-agentops", IsNew: false, "family blackout overlap"),
            new DigestItem("great-lakes", IsNew: false, "prep congestion"),
        };

        var n = TrackerDigest.Build([], conflicts);

        Assert.Contains("2 conflicts", n.Subject);
        Assert.Contains("Conflict flags (2)", n.HtmlBody);
        Assert.Contains("family blackout overlap", n.HtmlBody);
    }

    [Fact]
    public void Build_combines_category_changes_and_conflicts_in_subject()
    {
        var n = TrackerDigest.Build(
            [new DigestItem("a", IsNew: false, "SubmitNow → Submitted")],
            [new DigestItem("b", IsNew: false, "family blackout overlap")]);

        Assert.Contains("1 changed", n.Subject);
        Assert.Contains("1 conflict", n.Subject);
    }
}
