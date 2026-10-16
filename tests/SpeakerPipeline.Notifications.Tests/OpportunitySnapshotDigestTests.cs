using SpeakerPipeline.Core;

namespace SpeakerPipeline.Notifications.Tests;

public sealed class OpportunitySnapshotDigestTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-31T18:00:00Z");

    [Fact]
    public void Weekly_groups_use_current_verdict_and_preserve_pinned_status_as_context()
    {
        var pinnedSubmit = Opportunity("old-submit", Recommendation.Monitor, OpportunityStatus.SubmitNow);
        var pinnedMonitor = Opportunity("new-submit", Recommendation.SubmitNow, OpportunityStatus.Monitor);
        var outreach = Opportunity("outreach", Recommendation.Outreach, OpportunityStatus.Drafting);
        var notice = OpportunitySnapshotDigest.Build([pinnedSubmit, outreach, pinnedMonitor], new Dictionary<string, OpportunityVerdictChange>(), Now);
        Assert.Equal(new[] { "SubmitNow", "Outreach" }, notice.Groups.Select(g => g.Title));
        Assert.Equal("new-submit", Assert.Single(notice.Groups[0].Items).Id);
        Assert.Contains("Recommendation: SubmitNow · Your status: Monitor", notice.Groups[0].Items[0].Detail);
        Assert.Equal("outreach", Assert.Single(notice.Groups[1].Items).Id);
        Assert.DoesNotContain(notice.Groups.SelectMany(g => g.Items), i => i.Id == "old-submit" || i.Id == "monitor");
        Assert.Contains("1 SubmitNow · 1 Outreach · 1 Monitor", notice.Subject);
        Assert.Equal(OpportunityStatus.SubmitNow, pinnedSubmit.Status);
        Assert.Equal(OpportunityStatus.Monitor, pinnedMonitor.Status);
    }

    [Fact]
    public void Snapshot_prefers_fit_rationale_and_includes_change_explanation_once()
    {
        var opportunity = Opportunity("changed", Recommendation.SubmitNow, OpportunityStatus.Monitor);
        opportunity = opportunity with { LatestDecision = opportunity.LatestDecision! with { FitRationale = "Audience matches the workshop.", Rationale = "Legacy general rationale." } };
        var notice = OpportunitySnapshotDigest.Build([opportunity], new Dictionary<string, OpportunityVerdictChange>
        { [opportunity.Slug] = new(EventCategory.Monitor, Recommendation.SubmitNow, "CFP opened with a suitable track.") }, Now);
        var item = Assert.Single(Assert.Single(notice.Groups).Items);
        Assert.Contains("Why: Audience matches the workshop.", item.Detail);
        Assert.DoesNotContain("Legacy general rationale.", item.Detail);
        Assert.Equal(1, item.Detail.Split("CFP opened with a suitable track.").Length - 1);
        Assert.Contains("Verdict changed: Monitor → SubmitNow.", item.Detail);
    }

    [Fact]
    public void Monitor_only_snapshot_has_summary_without_numbered_items()
    {
        var notice = OpportunitySnapshotDigest.Build([Opportunity("monitor", Recommendation.Monitor, OpportunityStatus.SubmitNow)],
            new Dictionary<string, OpportunityVerdictChange>(), Now);
        Assert.Empty(notice.Groups);
        Assert.Contains("0 SubmitNow · 0 Outreach · 1 Monitor", notice.Subject);
        Assert.Contains("No actionable recommendations", notice.HtmlBody);
    }

    [Fact]
    public void Snapshot_excludes_snoozed_and_recorded_submissions_and_sorts_by_deadline()
    {
        var first = Opportunity("first", Recommendation.SubmitNow, OpportunityStatus.Drafting);
        var second = Opportunity("second", Recommendation.SubmitNow, OpportunityStatus.New);
        second = second with { Event = second.Event with { CfpDeadline = Now.AddDays(3) } };
        var notice = OpportunitySnapshotDigest.Build([second,
            Opportunity("snoozed", Recommendation.SubmitNow, OpportunityStatus.Monitor) with { SnoozedUntilUtc = Now.AddDays(7) },
            Opportunity("submitted", Recommendation.SubmitNow, OpportunityStatus.Monitor) with { RecordedSubmissionStatus = SubmissionStatus.Submitted }, first],
            new Dictionary<string, OpportunityVerdictChange>(), Now);
        Assert.Equal(new[] { "first", "second" }, Assert.Single(notice.Groups).Items.Select(i => i.Id));
    }

    private static OpportunityRecord Opportunity(string slug, Recommendation verdict, OpportunityStatus status) => new()
    {
        Event = new EventRecord { Slug = slug, Name = slug, EventType = EventType.Conference,
            Category = EventCategory.SubmitNow, Priority = Priority.High, CfpDeadline = Now.AddDays(1),
            CfpUrl = "https://example.test/cfp", CfpStatus = CfpStatus.Open },
        SystemVerdict = verdict, UserStatus = status, PlacementSource = PlacementSource.User,
        LatestDecision = new ScoringDecision { EventSlug = slug, Recommendation = verdict, Rationale = "A good fit.", FitScore = 8, EffortScore = 2, ConfidenceScore = 8 },
    };
}
