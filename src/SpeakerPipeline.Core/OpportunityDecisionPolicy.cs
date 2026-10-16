namespace SpeakerPipeline.Core;

public static class OpportunityDecisionPolicy
{
    public static void Validate(OpportunityRecord current, OpportunityScoringRequest request, DateTimeOffset now, CatalogSnapshot? catalog = null)
    {
        var decision = request.Decision;
        ScoringDecision.ValidateScore(decision.FitScore, nameof(decision.FitScore));
        ScoringDecision.ValidateScore(decision.EffortScore, nameof(decision.EffortScore));
        ScoringDecision.ValidateScore(decision.ConfidenceScore, nameof(decision.ConfidenceScore));
        if (!Enum.IsDefined(decision.Recommendation) || string.IsNullOrWhiteSpace(decision.Rationale))
            throw new ArgumentException("A defined verdict and rationale are required.");
        if (decision.InputRevision is { } input && input != request.ScoringInputRevision.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new ArgumentException("Decision input revision must match the scoring request.");
        if (decision.Recommendation is Recommendation.SubmitNow or Recommendation.Outreach)
        {
            if (current.Event.FamilyConflictFlag) throw new ArgumentException("A hard calendar conflict blocks an actionable verdict.");
            if (catalog is not null && !current.RepeatTalkOverride && decision.MatchedTalks.Any(m => CatalogRepeatPolicy.IsRepeat(current.Event, m.TalkId, catalog)))
                throw new ArgumentException("A completed talk at this event series requires an explicit repeat-talk override.");
        }
        if (decision.Recommendation == Recommendation.SubmitNow &&
            (!Uri.TryCreate(current.Event.CfpUrl, UriKind.Absolute, out var cfp) || cfp.Scheme is not ("http" or "https") ||
             current.Event.CfpStatus is CfpStatus.Closed or CfpStatus.NotYetOpen or CfpStatus.RevalidateLive || current.Event.CfpDeadline <= now ||
             current.Event.CfpStatus != CfpStatus.Open && !(current.Event.CfpDeadline > now)))
            throw new ArgumentException("SubmitNow requires a valid CFP URL plus observed Open status or a future deadline, and cannot be closed, expired or awaiting revalidation.");
    }
}
