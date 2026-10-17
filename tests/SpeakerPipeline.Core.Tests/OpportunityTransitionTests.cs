using SpeakerPipeline.Core;

namespace SpeakerPipeline.Core.Tests;

public sealed class OpportunityTransitionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-16T15:00:00Z");
    [Theory]
    [InlineData(EventCategory.Pass, OpportunityStatus.NotPursuing, false)]
    [InlineData(EventCategory.Skip, OpportunityStatus.Dropped, false)]
    [InlineData(EventCategory.Accepted, OpportunityStatus.Accepted, true)]
    [InlineData(EventCategory.Quarantine, OpportunityStatus.Review, false)]
    public void Import_preserves_placement_and_independent_suppression(EventCategory category, OpportunityStatus status, bool suppressed)
    {
        var imported = OpportunityMapping.Import(Event(category) with { DoNotResurface = suppressed });
        Assert.Equal(status, imported.Status);
        Assert.Equal(suppressed, imported.DoNotResurface);
        Assert.Equal(PlacementSource.Migration, imported.PlacementSource);
        Assert.Null(imported.UserTouchedAtUtc);
        Assert.Null(imported.LatestDecision);
        Assert.Equal(category, OpportunityMapping.Project(imported).Category);
    }
    [Fact]
    public void Machine_skip_is_reversible_and_user_position_wins()
    {
        var automatic = new OpportunityRecord { Event = Event(), SystemVerdict = Recommendation.Skip };
        Assert.Equal(OpportunityStatus.NotPursuing, automatic.Status);
        Assert.False(automatic.DoNotResurface);
        var submitted = OpportunityTransitions.Apply(automatic, Request(OpportunityAction.Submitted), Now);
        Assert.Equal(OpportunityStatus.Submitted, (submitted with { SystemVerdict = Recommendation.SubmitNow }).Status);
    }
    [Fact]
    public void Snooze_is_overlay_and_drop_restore_are_explicit()
    {
        var original = OpportunityMapping.Import(Event(EventCategory.SubmitNow));
        var snoozed = OpportunityTransitions.Apply(original, Request(OpportunityAction.Snooze7Days), Now);
        Assert.Equal(original.Status, snoozed.Status);
        Assert.Equal(Now.AddDays(7), snoozed.SnoozedUntilUtc);
        Assert.True(snoozed.IsSnoozed(Now.AddDays(6)));
        Assert.False(snoozed.IsSnoozed(Now.AddDays(7)));
        Assert.Throws<ArgumentException>(() => OpportunityTransitions.Apply(original, Request(OpportunityAction.Drop), Now));
        var dropped = OpportunityTransitions.Apply(original, Request(OpportunityAction.Drop) with { ReasonCode = FeedbackReason.TooFar }, Now);
        Assert.True(dropped.DoNotResurface);
        Assert.Throws<ArgumentException>(() => OpportunityTransitions.Apply(dropped, Request(OpportunityAction.FollowPipeline), Now));
        Assert.False(OpportunityTransitions.Apply(dropped, Request(OpportunityAction.Restore), Now).DoNotResurface);
    }
    [Fact]
    public void Demotion_requires_reason_and_override_is_not_feedback()
    {
        var original = OpportunityMapping.Import(Event(EventCategory.SubmitNow));
        Assert.Throws<ArgumentException>(() => OpportunityTransitions.Apply(original, Request(OpportunityAction.SetStatus) with { Status = OpportunityStatus.Monitor }, Now));
        var changed = OpportunityTransitions.Apply(original, Request(OpportunityAction.SetRepeatTalkOverride) with { RepeatTalkOverride = true }, Now);
        Assert.True(changed.RepeatTalkOverride);
        Assert.Equal(original.ScoringInputRevision + 1, changed.ScoringInputRevision);
    }
    [Theory]
    [InlineData(OpportunityStatus.Submitted, OpportunityStatus.Monitor)]
    [InlineData(OpportunityStatus.Accepted, OpportunityStatus.Outreach)]
    [InlineData(OpportunityStatus.Delivered, OpportunityStatus.SubmitNow)]
    [InlineData(OpportunityStatus.Accepted, OpportunityStatus.Submitted)]
    public void Moving_backwards_from_workflow_progress_requires_explicit_reason(OpportunityStatus from, OpportunityStatus to)
    {
        var original = new OpportunityRecord { Event = Event(), UserStatus = from };
        var request = Request(OpportunityAction.SetStatus) with { Status = to };
        Assert.Throws<ArgumentException>(() => OpportunityTransitions.Apply(original, request, Now));
        var changed = OpportunityTransitions.Apply(original, request with { ReasonCode = FeedbackReason.BadTiming }, Now);
        Assert.Equal(to, changed.Status);
        Assert.Equal(original.ScoringInputRevision + 1, changed.ScoringInputRevision);
    }
    [Fact]
    public void Reopening_rejected_opportunity_is_not_a_workflow_demotion()
    {
        var rejected = new OpportunityRecord { Event = Event(), UserStatus = OpportunityStatus.Rejected };
        Assert.Equal(OpportunityStatus.Monitor, OpportunityTransitions.Apply(rejected,
            Request(OpportunityAction.SetStatus) with { Status = OpportunityStatus.Monitor }, Now).Status);
    }
    private static OpportunityActionRequest Request(OpportunityAction action) => new() { Action = action, IdempotencyKey = Guid.NewGuid().ToString("n") };
    internal static EventRecord Event(EventCategory category = EventCategory.Monitor) => new() { Slug = "fictional-event", Name = "Fictional event", EventType = EventType.Conference, Category = category, Priority = Priority.Medium };
}
