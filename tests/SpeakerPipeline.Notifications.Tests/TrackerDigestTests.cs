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
}
