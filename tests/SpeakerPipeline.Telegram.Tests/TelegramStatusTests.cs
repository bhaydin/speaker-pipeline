using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Core;
using SpeakerPipeline.Notifications;

namespace SpeakerPipeline.Telegram.Tests;

public sealed class TelegramStatusTests
{
    [Fact]
    public async Task Status_uses_current_recommendations_and_shows_pinned_placement_separately()
    {
        var oldPin = Opportunity("old-pin", Recommendation.Monitor, OpportunityStatus.SubmitNow);
        var newRecommendation = Opportunity("new-recommendation", Recommendation.SubmitNow, OpportunityStatus.Monitor);

        var reply = await Status(oldPin, newRecommendation);

        Assert.Contains("Board status: Monitor 1  ·  SubmitNow 1", reply);
        Assert.Contains("Recommendations: 1 SubmitNow · 0 Outreach · 1 Monitor", reply);
        Assert.Contains("new-recommendation", reply);
        Assert.Contains("Recommendation: SubmitNow · Your status: Monitor", reply);
        Assert.DoesNotContain("old-pin", reply);
        Assert.Equal(OpportunityStatus.SubmitNow, oldPin.Status);
        Assert.Equal(OpportunityStatus.Monitor, newRecommendation.Status);
    }

    [Theory]
    [InlineData(CfpStatus.Closed, 7, true)]
    [InlineData(CfpStatus.NotYetOpen, 7, true)]
    [InlineData(CfpStatus.RevalidateLive, 7, true)]
    [InlineData(CfpStatus.Open, -1, true)]
    [InlineData(CfpStatus.Open, 7, false)]
    public async Task Status_excludes_submit_now_without_current_cfp_evidence(CfpStatus status, int deadlineDays, bool hasCfpUrl)
    {
        var opportunity = Opportunity("ineligible-cfp", Recommendation.SubmitNow, OpportunityStatus.SubmitNow);
        opportunity = opportunity with { Event = opportunity.Event with
        { CfpStatus = status, CfpDeadline = DateTimeOffset.UtcNow.AddDays(deadlineDays), CfpUrl = hasCfpUrl ? "https://example.test/cfp" : null } };

        var reply = await Status(opportunity);

        Assert.Contains("Board status: SubmitNow 1", reply);
        Assert.Contains("Recommendations: 0 SubmitNow · 0 Outreach · 0 Monitor", reply);
        Assert.DoesNotContain("<b>Actionable</b>", reply);
        Assert.DoesNotContain("ineligible-cfp", reply);
    }

    [Theory]
    [InlineData(SubmissionStatus.Submitted)]
    [InlineData(SubmissionStatus.InReview)]
    [InlineData(SubmissionStatus.Accepted)]
    public async Task Recorded_submission_suppresses_actionable_even_when_pinned_status_hides_it(SubmissionStatus submission)
    {
        var opportunity = Opportunity("already-submitted", Recommendation.SubmitNow, OpportunityStatus.SubmitNow)
            with { RecordedSubmissionStatus = submission };

        var reply = await Status(opportunity);

        Assert.Contains("Board status: SubmitNow 1", reply);
        Assert.Contains("Recommendations: 0 SubmitNow · 0 Outreach · 0 Monitor", reply);
        Assert.DoesNotContain("already-submitted", reply);
    }

    [Fact]
    public async Task Status_honors_snooze_drop_do_not_resurface_and_family_conflict()
    {
        var snoozed = Opportunity("snoozed", Recommendation.SubmitNow) with { SnoozedUntilUtc = DateTimeOffset.UtcNow.AddDays(7) };
        var dropped = Opportunity("dropped", Recommendation.SubmitNow, OpportunityStatus.Dropped);
        var hidden = Opportunity("hidden", Recommendation.Outreach) with { DoNotResurface = true };
        var conflict = Opportunity("conflict", Recommendation.SubmitNow);
        conflict = conflict with { Event = conflict.Event with { FamilyConflictFlag = true } };

        var reply = await Status(snoozed, dropped, hidden, conflict);

        Assert.Contains("Recommendations: 0 SubmitNow · 0 Outreach · 0 Monitor", reply);
        Assert.DoesNotContain("<b>Actionable</b>", reply);
    }

    [Fact]
    public async Task Actionable_order_matches_snapshot_groups_and_deadlines_unknown_last()
    {
        var lateSubmit = Opportunity("late-submit", Recommendation.SubmitNow);
        lateSubmit = lateSubmit with { Event = lateSubmit.Event with { CfpDeadline = DateTimeOffset.UtcNow.AddDays(20) } };
        var earlySubmit = Opportunity("early-submit", Recommendation.SubmitNow);
        var datedOutreach = Opportunity("dated-outreach", Recommendation.Outreach);
        datedOutreach = datedOutreach with { Event = datedOutreach.Event with { CfpDeadline = DateTimeOffset.UtcNow.AddDays(1) } };
        var unknownOutreach = Opportunity("unknown-outreach", Recommendation.Outreach);
        unknownOutreach = unknownOutreach with { Event = unknownOutreach.Event with { CfpDeadline = null } };

        var reply = await Status(unknownOutreach, datedOutreach, lateSubmit, earlySubmit);

        Assert.Contains("Recommendations: 2 SubmitNow · 2 Outreach · 0 Monitor", reply);
        Assert.True(reply.IndexOf("early-submit", StringComparison.Ordinal) < reply.IndexOf("late-submit", StringComparison.Ordinal));
        Assert.True(reply.IndexOf("late-submit", StringComparison.Ordinal) < reply.IndexOf("dated-outreach", StringComparison.Ordinal));
        Assert.True(reply.IndexOf("dated-outreach", StringComparison.Ordinal) < reply.IndexOf("unknown-outreach", StringComparison.Ordinal));
    }

    private static OpportunityRecord Opportunity(string slug, Recommendation verdict, OpportunityStatus? pinned = null) => new()
    {
        Event = new EventRecord { Slug = slug, Name = slug, EventType = EventType.Conference, Priority = Priority.Medium,
            Category = EventCategory.SubmitNow, CfpStatus = CfpStatus.Open, CfpUrl = "https://example.test/cfp",
            CfpDeadline = DateTimeOffset.UtcNow.AddDays(7) },
        SystemVerdict = verdict, UserStatus = pinned,
    };

    private static async Task<string> Status(params OpportunityRecord[] snapshot)
    {
        var router = new TelegramCommandRouter(new FakeApiClient(), new FakeIngestService(),
            Options.Create(new TelegramOptions { Enabled = true, ChatId = 7 }), NullLogger<TelegramCommandRouter>.Instance,
            opportunities: new ReadOnlyOpportunities(snapshot), pipeline: Options.Create(new PipelineNotificationOptions { OpportunitiesEnabled = true }));
        var reply = await router.RouteAsync(new TelegramUpdate
        { Message = new TelegramMessage { Text = "/status", Chat = new TelegramChat { Id = 7, Type = "private" } } });
        return Assert.IsType<TelegramReply>(reply).Text;
    }

    private sealed class ReadOnlyOpportunities(IReadOnlyList<OpportunityRecord> snapshot) : IOpportunityApiClient
    {
        public Task<IReadOnlyList<OpportunityRecord>> GetOpportunitiesAsync(CancellationToken ct = default) => Task.FromResult(snapshot);
        public Task<OpportunityRecord?> GetOpportunityAsync(string slug, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<OpportunityHistoryEntry>> GetOpportunityHistoryAsync(string slug, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<OpportunityHistoryEntry>> GetOpportunityFeedbackAsync(int limit = 20, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpportunityRecord> ApplyOpportunityActionAsync(string slug, OpportunityActionRequest request, string expectedETag, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OpportunityRecord> PostOpportunityDecisionAsync(OpportunityScoringRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
