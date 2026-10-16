using SpeakerPipeline.Core;

namespace SpeakerPipeline.Notifications.Tests;

public sealed class OpportunityNotificationPolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-31T18:00:00Z");

    [Theory]
    [InlineData(48, true)]
    [InlineData(47, true)]
    [InlineData(49, false)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void Snooze_breakthrough_has_exact_48_hour_window(int hours, bool expected)
        => Assert.Equal(expected, OpportunityNotificationPolicy.NeedsSnoozeRescue(Record(hours), Now));

    [Theory]
    [InlineData(OpportunityStatus.Submitted)]
    [InlineData(OpportunityStatus.Dropped)]
    [InlineData(OpportunityStatus.Accepted)]
    [InlineData(OpportunityStatus.Delivered)]
    [InlineData(OpportunityStatus.NotPursuing)]
    [InlineData(OpportunityStatus.Rejected)]
    public void Nonactionable_states_never_get_rescue(OpportunityStatus status)
        => Assert.False(OpportunityNotificationPolicy.NeedsSnoozeRescue(Record(24) with { UserStatus = status }, Now));

    [Fact]
    public void Expired_snooze_and_do_not_resurface_never_get_rescue()
    {
        Assert.False(OpportunityNotificationPolicy.NeedsSnoozeRescue(Record(24) with { SnoozedUntilUtc = Now }, Now));
        Assert.False(OpportunityNotificationPolicy.NeedsSnoozeRescue(Record(24) with { DoNotResurface = true }, Now));
    }

    [Fact]
    public void Pinned_submit_now_uses_current_monitor_verdict_for_snapshot_and_rescue()
    {
        var opportunity = Record(24) with { SystemVerdict = Recommendation.Monitor, PlacementSource = PlacementSource.Migration };
        Assert.False(OpportunityNotificationPolicy.NeedsSnoozeRescue(opportunity, Now));
        Assert.Equal(Recommendation.Monitor, OpportunityNotificationPolicy.SnapshotVerdict(opportunity with { SnoozedUntilUtc = null }, Now));
        Assert.Equal(OpportunityStatus.SubmitNow, opportunity.Status);
    }

    [Theory]
    [InlineData(OpportunityStatus.New)]
    [InlineData(OpportunityStatus.Monitor)]
    [InlineData(OpportunityStatus.Drafting)]
    public void Manual_placement_does_not_hide_current_actionable_verdict(OpportunityStatus status)
    {
        var opportunity = Record(24) with { UserStatus = status, SystemVerdict = Recommendation.SubmitNow, PlacementSource = PlacementSource.User };
        Assert.True(OpportunityNotificationPolicy.NeedsSnoozeRescue(opportunity, Now));
        Assert.Null(OpportunityNotificationPolicy.SnapshotVerdict(opportunity, Now));
        Assert.Equal(Recommendation.SubmitNow, OpportunityNotificationPolicy.SnapshotVerdict(opportunity with { SnoozedUntilUtc = null }, Now));
        Assert.Equal(status, opportunity.Status);
    }

    [Theory]
    [InlineData(SubmissionStatus.Submitted)]
    [InlineData(SubmissionStatus.InReview)]
    [InlineData(SubmissionStatus.Accepted)]
    public void Recorded_submission_suppresses_notification_even_when_owner_pin_hides_its_status(SubmissionStatus status)
    {
        var opportunity = Record(24) with { UserStatus = OpportunityStatus.Monitor, SystemVerdict = Recommendation.SubmitNow, RecordedSubmissionStatus = status };
        Assert.Equal(OpportunityStatus.Monitor, opportunity.Status);
        Assert.Null(OpportunityNotificationPolicy.NotificationVerdict(opportunity, Now));
        Assert.False(OpportunityNotificationPolicy.NeedsSnoozeRescue(opportunity, Now));
    }

    [Theory]
    [InlineData(CfpStatus.Closed)]
    [InlineData(CfpStatus.NotYetOpen)]
    [InlineData(CfpStatus.RevalidateLive)]
    public void Legacy_and_current_submit_now_require_current_cfp_evidence(CfpStatus status)
    {
        var opportunity = Record(24);
        opportunity = opportunity with { Event = opportunity.Event with { CfpStatus = status }, SnoozedUntilUtc = null };
        Assert.Null(OpportunityNotificationPolicy.SnapshotVerdict(opportunity, Now));
        Assert.Null(OpportunityNotificationPolicy.SnapshotVerdict(opportunity with { SystemVerdict = Recommendation.SubmitNow }, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Expired_legacy_submit_now_never_appears_as_actionable(int hours)
        => Assert.Null(OpportunityNotificationPolicy.SnapshotVerdict(Record(hours) with { SnoozedUntilUtc = null }, Now));

    [Fact]
    public void Legacy_fallback_does_not_override_a_current_verdict()
    {
        var opportunity = Record(24) with { LegacyCategory = EventCategory.Outreach, SnoozedUntilUtc = null };
        Assert.Equal(Recommendation.Outreach, OpportunityNotificationPolicy.SnapshotVerdict(opportunity, Now));
        Assert.Equal(Recommendation.Monitor, OpportunityNotificationPolicy.SnapshotVerdict(opportunity with { SystemVerdict = Recommendation.Monitor }, Now));
        Assert.False(OpportunityNotificationPolicy.IsActionable(opportunity with { SystemVerdict = Recommendation.Skip }, Now));
    }

    [Fact]
    public async Task Rescue_receipt_survives_month_boundary_and_repeated_snoozes()
    {
        var coordinator = new DeliveryCoordinator(new MemoryOperations(), TimeProvider.System);
        var opportunity = Record(36);
        var key = OpportunityNotificationPolicy.SnoozeRescueKey(opportunity.Slug, opportunity.Event.CfpDeadline!.Value);
        var sends = 0;
        Task<NotificationDelivery> Send() { sends++; return Task.FromResult(new NotificationDelivery("Sent")); }
        await coordinator.DeliverAsync(key + ":Telegram:0", Send, default);
        await coordinator.DeliverAsync(key + ":Telegram:0", Send, default);
        Assert.Equal(1, sends);
    }

    [Fact]
    public void Weekly_identity_uses_Chicago_week_and_changes_next_week()
    {
        Assert.Equal(PipelineHealth.WeekKey(DateTimeOffset.Parse("2026-10-18T23:00:00Z")),
            PipelineHealth.WeekKey(DateTimeOffset.Parse("2026-10-19T04:59:00Z")));
        Assert.NotEqual(PipelineHealth.WeekKey(DateTimeOffset.Parse("2026-10-19T04:59:00Z")),
            PipelineHealth.WeekKey(DateTimeOffset.Parse("2026-10-19T05:00:00Z")));
    }

    private static OpportunityRecord Record(int hours) => new()
    {
        Event = new EventRecord { Slug = "conference", Name = "Conference", EventType = EventType.Conference,
            Category = EventCategory.SubmitNow, Priority = Priority.High, CfpDeadline = Now.AddHours(hours),
            CfpUrl = "https://example.test/cfp", CfpStatus = CfpStatus.Open },
        UserStatus = OpportunityStatus.SubmitNow, SnoozedUntilUtc = Now.AddDays(7),
    };
}
