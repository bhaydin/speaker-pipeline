namespace SpeakerPipeline.Core;

/// <summary>Pure action rules shared by storage tests; user placement never follows later model verdicts.</summary>
public static class OpportunityTransitions
{
    public static OpportunityRecord Apply(OpportunityRecord current, OpportunityActionRequest request, DateTimeOffset now)
    {
        if (!Enum.IsDefined(request.Action) || (request.Status is { } status && !Enum.IsDefined(status)) ||
            (request.ReasonCode is { } reason && !Enum.IsDefined(reason)))
            throw new ArgumentException("Unknown action, status or feedback reason.");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200)
            throw new ArgumentException("An idempotency key of at most 200 characters is required.");
        if (request.Note?.Length > 8000) throw new ArgumentException("Notes must be at most 8000 characters.");
        if (request.ReasonCode is not null && request.Action is not (OpportunityAction.Feedback or OpportunityAction.Drop or OpportunityAction.SetStatus))
            throw new ArgumentException("Feedback reasons belong to feedback, demotion or Drop actions.");
        if (request.Action == OpportunityAction.Drop && request.ReasonCode is null)
            throw new ArgumentException("Drop requires a feedback reason.");
        if (request.Action == OpportunityAction.Feedback && request.Agree is null)
            throw new ArgumentException("Feedback requires agree or disagree.");
        if (request.Action == OpportunityAction.SetStatus && request.Status is null)
            throw new ArgumentException("SetStatus requires a status.");
        if (request.Action == OpportunityAction.SetStatus && request.Status == OpportunityStatus.Dropped)
            throw new ArgumentException("Use the Drop action with a reason.");
        if (request.Action == OpportunityAction.SetStatus && request.Status is { } destination &&
            Rank(current.Status) > 0 && Rank(destination) >= 0 && Rank(destination) < Rank(current.Status) && request.ReasonCode is null)
            throw new ArgumentException("Demoting an opportunity requires a feedback reason.");
        if (request.Action == OpportunityAction.SetRepeatTalkOverride && request.RepeatTalkOverride is null)
            throw new ArgumentException("An explicit repeat-talk preference is required.");
        if (request.RepeatTalkOverride is not null && request.Action != OpportunityAction.SetRepeatTalkOverride)
            throw new ArgumentException("Use the dedicated repeat-talk override action.");
        if (request.Action == OpportunityAction.RetireFeedback && string.IsNullOrWhiteSpace(request.FeedbackId))
            throw new ArgumentException("Retiring feedback requires its ID.");
        if (request.DecisionId is not null && request.DecisionId != current.LatestDecisionId)
            throw new OpportunityConflictException("Feedback targets a superseded decision. Refresh before replying.", current);

        var updated = request.Action switch
        {
            OpportunityAction.SetStatus => current with { UserStatus = request.Status, PlacementSource = PlacementSource.User, UserTouchedAtUtc = now },
            OpportunityAction.Submitted => current with { UserStatus = OpportunityStatus.Submitted, PlacementSource = PlacementSource.User, UserTouchedAtUtc = now, SnoozedUntilUtc = null },
            OpportunityAction.Snooze7Days => current with { SnoozedUntilUtc = now.AddDays(7) },
            OpportunityAction.Drop => current with { UserStatus = OpportunityStatus.Dropped, PlacementSource = PlacementSource.User, UserTouchedAtUtc = now, DoNotResurface = true, SnoozedUntilUtc = null },
            OpportunityAction.FollowPipeline when !current.DoNotResurface => current with { UserStatus = null, PlacementSource = PlacementSource.Pipeline, UserTouchedAtUtc = now },
            OpportunityAction.FollowPipeline => throw new ArgumentException("Restore a suppressed opportunity before following the pipeline."),
            OpportunityAction.Restore => current with { UserStatus = OpportunityStatus.Monitor, PlacementSource = PlacementSource.User, UserTouchedAtUtc = now, DoNotResurface = false, SnoozedUntilUtc = null },
            OpportunityAction.SaveNotes => current with { UserNotes = request.Note },
            _ => current,
        };
        if (request.RepeatTalkOverride is { } repeat)
            updated = updated with { RepeatTalkOverride = repeat, ScoringInputRevision = updated.ScoringInputRevision + 1 };
        if (request.Action is OpportunityAction.Feedback or OpportunityAction.RetireFeedback || request.ReasonCode is not null)
            updated = updated with { ScoringInputRevision = updated.ScoringInputRevision + 1 };
        return updated with { LastSeenDecisionId = current.LatestDecisionId };
    }

    private static int Rank(OpportunityStatus status) => status switch
    {
        OpportunityStatus.New or OpportunityStatus.NotPursuing => 0,
        OpportunityStatus.Monitor => 1, OpportunityStatus.Outreach => 2,
        OpportunityStatus.SubmitNow => 3, OpportunityStatus.Drafting => 4,
        OpportunityStatus.Submitted => 5, OpportunityStatus.Accepted => 6, OpportunityStatus.Delivered => 7,
        _ => -1,
    };
}
