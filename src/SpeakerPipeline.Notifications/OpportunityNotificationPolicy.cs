using SpeakerPipeline.Core;

namespace SpeakerPipeline.Notifications;

public static class OpportunityNotificationPolicy
{
    /// <summary>The current recommendation that may appear in notifications, independent of pinned placement.</summary>
    public static Recommendation? NotificationVerdict(OpportunityRecord opportunity, DateTimeOffset now)
    {
        if (opportunity.DoNotResurface || opportunity.Status is OpportunityStatus.Submitted or OpportunityStatus.Accepted
                or OpportunityStatus.Delivered or OpportunityStatus.Dropped or OpportunityStatus.NotPursuing or OpportunityStatus.Rejected
            || opportunity.RecordedSubmissionStatus is SubmissionStatus.Submitted or SubmissionStatus.InReview or SubmissionStatus.Accepted)
            return null;

        var verdict = opportunity.SystemVerdict ?? (opportunity.LegacyCategory ?? opportunity.Event.Category) switch
        {
            EventCategory.SubmitNow => Recommendation.SubmitNow,
            EventCategory.Outreach => Recommendation.Outreach,
            EventCategory.Monitor => Recommendation.Monitor,
            _ => (Recommendation?)null,
        };
        if (verdict is Recommendation.SubmitNow or Recommendation.Outreach && opportunity.Event.FamilyConflictFlag)
            return null;
        // Re-check current facts: a legacy pin or a decision made before a deadline
        // passed must never advertise an expired/closed/unverified CFP as SubmitNow.
        var ev = opportunity.Event;
        if (verdict == Recommendation.SubmitNow &&
            (!Uri.TryCreate(ev.CfpUrl, UriKind.Absolute, out var cfp) || cfp.Scheme is not ("http" or "https") ||
             ev.CfpStatus is CfpStatus.Closed or CfpStatus.NotYetOpen or CfpStatus.RevalidateLive || ev.CfpDeadline <= now ||
             ev.CfpStatus != CfpStatus.Open && !(ev.CfpDeadline > now)))
            return null;
        return verdict;
    }

    public static bool IsActionable(OpportunityRecord opportunity, DateTimeOffset now)
        => NotificationVerdict(opportunity, now) is Recommendation.SubmitNow or Recommendation.Outreach;

    public static Recommendation? SnapshotVerdict(OpportunityRecord opportunity, DateTimeOffset now)
        => opportunity.IsSnoozed(now) ? null : NotificationVerdict(opportunity, now);

    public static bool NeedsSnoozeRescue(OpportunityRecord opportunity, DateTimeOffset now)
        => IsActionable(opportunity, now) && opportunity.IsSnoozed(now)
            && opportunity.Event.CfpDeadline is { } deadline && deadline > now && deadline <= now.AddHours(48);

    // Persistent receipts use this identity across months and repeated runs; snoozing again
    // does not re-arm the same deadline rescue.
    public static string SnoozeRescueKey(string slug, DateTimeOffset deadline)
        => $"snooze-rescue-{slug}-{deadline:yyyyMMdd}";
}
