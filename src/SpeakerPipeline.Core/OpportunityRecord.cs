namespace SpeakerPipeline.Core;

public enum OpportunityStatus { New, Monitor, Outreach, SubmitNow, Drafting, Submitted, Accepted, NotPursuing, Delivered, Rejected, Dropped, Review }
public enum PlacementSource { Pipeline, User, Migration }
public enum OpportunityAction { SetStatus, Submitted, Snooze7Days, Drop, FollowPipeline, Restore, SaveNotes, Feedback, SetRepeatTalkOverride, RetireFeedback }
public enum FeedbackReason { WrongAudience, TooFar, BadTiming, AlreadySpokeThere, NotMyTopic, Other }

/// <summary>The authoritative opportunity aggregate. ETag applies to facts and user/system state together.</summary>
public sealed record OpportunityRecord
{
    public required EventRecord Event { get; init; }
    public string Slug => Event.Slug;
    public string ETag { get; init; } = "";
    public long ScoringInputRevision { get; init; } = 1;
    public Recommendation? SystemVerdict { get; init; }
    public ScoringDecision? LatestDecision { get; init; }
    public string? LatestDecisionId { get; init; }
    public string? LastSeenDecisionId { get; init; }
    public OpportunityStatus? UserStatus { get; init; }
    public PlacementSource PlacementSource { get; init; }
    public EventCategory? LegacyCategory { get; init; }
    public DateTimeOffset? UserTouchedAtUtc { get; init; }
    public DateTimeOffset? SnoozedUntilUtc { get; init; }
    public string? UserNotes { get; init; }
    public bool DoNotResurface { get; init; }
    public bool RepeatTalkOverride { get; init; }
    public SubmissionStatus? RecordedSubmissionStatus { get; init; }
    public OpportunityStatus Status => UserStatus ?? RecordedSubmissionStatus switch
    {
        SubmissionStatus.Accepted => OpportunityStatus.Accepted,
        SubmissionStatus.Submitted or SubmissionStatus.InReview => OpportunityStatus.Submitted,
        SubmissionStatus.Rejected => OpportunityStatus.Rejected,
        SubmissionStatus.Withdrawn => OpportunityStatus.NotPursuing,
        _ => PipelineStatus,
    };
    private OpportunityStatus PipelineStatus => SystemVerdict switch
    {
        Recommendation.SubmitNow => OpportunityStatus.SubmitNow,
        Recommendation.Outreach => OpportunityStatus.Outreach,
        Recommendation.Monitor => OpportunityStatus.Monitor,
        Recommendation.Pass or Recommendation.Skip => OpportunityStatus.NotPursuing,
        _ => OpportunityStatus.New,
    };
    public bool IsSnoozed(DateTimeOffset now) => SnoozedUntilUtc > now;
}

public sealed record OpportunityActionRequest
{
    public required OpportunityAction Action { get; init; }
    public string IdempotencyKey { get; init; } = "";
    public OpportunityStatus? Status { get; init; }
    public FeedbackReason? ReasonCode { get; init; }
    public bool? Agree { get; init; }
    public string? Note { get; init; }
    public string? DecisionId { get; init; }
    public bool? RepeatTalkOverride { get; init; }
    public string? FeedbackId { get; init; }
    public string? SupersedesFeedbackId { get; init; }
}

public sealed record OpportunityHistoryEntry
{
    public string? EventSlug { get; init; }
    public string? EventName { get; init; }
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required DateTimeOffset AtUtc { get; init; }
    public required string Actor { get; init; }
    public Recommendation? PreviousVerdict { get; init; }
    public Recommendation? Verdict { get; init; }
    public OpportunityStatus? Status { get; init; }
    public string? Reason { get; init; }
    public ScoringDecision? Decision { get; init; }
    public EventRecord? EventSnapshot { get; init; }
    public FeedbackReason? ReasonCode { get; init; }
    public bool? Agree { get; init; }
    public string? DecisionId { get; init; }
    public string? SupersedesFeedbackId { get; init; }
}

public sealed record OpportunityScoringRequest
{
    public required ScoringDecision Decision { get; init; }
    public required string ExpectedETag { get; init; }
    public required long ScoringInputRevision { get; init; }
    public required string IdempotencyKey { get; init; }
}

public sealed class OpportunityConflictException(string message, OpportunityRecord? current = null, bool staleScoringInput = false) : Exception(message)
{
    public OpportunityRecord? Current { get; } = current;
    public bool StaleScoringInput { get; } = staleScoringInput;
}

public interface IOpportunityRepository
{
    Task<IReadOnlyList<OpportunityRecord>> GetAllAsync(CancellationToken ct = default);
    Task<OpportunityRecord?> GetAsync(string slug, CancellationToken ct = default);
    Task<IReadOnlyList<OpportunityHistoryEntry>> GetHistoryAsync(string slug, CancellationToken ct = default);
    Task<IReadOnlyList<OpportunityHistoryEntry>> GetFeedbackAsync(int limit = 20, CancellationToken ct = default);
    Task<OpportunityRecord> ApplyActionAsync(string slug, OpportunityActionRequest request, string expectedETag, string actor, CancellationToken ct = default);
    Task<OpportunityRecord> ApplyDecisionAsync(OpportunityScoringRequest request, CancellationToken ct = default);
    Task<OpportunityRecord> ApplyDecisionAsync(OpportunityScoringRequest request, CatalogSnapshot? catalog, CancellationToken ct = default)
        => ApplyDecisionAsync(request, ct);
    Task<OpportunityRecord> UpsertFactsAsync(EventRecord record, CancellationToken ct = default);
    Task<OpportunityRecord> ReconcileSubmissionsAsync(string slug, IReadOnlyList<SubmissionRecord> submissions, string expectedETag, CancellationToken ct = default)
        => throw new NotSupportedException();
}

public interface IOpportunityApiClient
{
    Task<IReadOnlyList<OpportunityRecord>> GetOpportunitiesAsync(CancellationToken ct = default);
    Task<OpportunityRecord?> GetOpportunityAsync(string slug, CancellationToken ct = default);
    Task<IReadOnlyList<OpportunityHistoryEntry>> GetOpportunityHistoryAsync(string slug, CancellationToken ct = default);
    Task<IReadOnlyList<OpportunityHistoryEntry>> GetOpportunityFeedbackAsync(int limit = 20, CancellationToken ct = default);
    Task<OpportunityRecord> ApplyOpportunityActionAsync(string slug, OpportunityActionRequest request, string expectedETag, CancellationToken ct = default);
    Task<OpportunityRecord> PostOpportunityDecisionAsync(OpportunityScoringRequest request, CancellationToken ct = default);
    Task<OpportunityRecord> ReconcileOpportunitySubmissionsAsync(string slug, string expectedETag, CancellationToken ct = default)
        => throw new NotSupportedException();
}

public static class OpportunityMapping
{
    public static OpportunityStatus FromLegacy(EventCategory category) => category switch
    {
        EventCategory.Delivered => OpportunityStatus.Delivered,
        EventCategory.Accepted => OpportunityStatus.Accepted,
        EventCategory.SubmitNow => OpportunityStatus.SubmitNow,
        EventCategory.Submitted => OpportunityStatus.Submitted,
        EventCategory.Outreach => OpportunityStatus.Outreach,
        EventCategory.Pass => OpportunityStatus.NotPursuing,
        EventCategory.Skip => OpportunityStatus.Dropped,
        EventCategory.Quarantine => OpportunityStatus.Review,
        _ => OpportunityStatus.Monitor,
    };

    public static EventCategory ToLegacy(OpportunityStatus status) => status switch
    {
        OpportunityStatus.Delivered => EventCategory.Delivered,
        OpportunityStatus.Accepted => EventCategory.Accepted,
        OpportunityStatus.SubmitNow or OpportunityStatus.Drafting => EventCategory.SubmitNow,
        OpportunityStatus.Submitted => EventCategory.Submitted,
        OpportunityStatus.Outreach => EventCategory.Outreach,
        OpportunityStatus.NotPursuing or OpportunityStatus.Rejected => EventCategory.Pass,
        OpportunityStatus.Dropped => EventCategory.Skip,
        OpportunityStatus.Review => EventCategory.Quarantine,
        _ => EventCategory.Monitor,
    };

    public static EventRecord Project(OpportunityRecord opportunity) => opportunity.Event with
    {
        ETag = opportunity.ETag,
        ScoringInputRevision = opportunity.ScoringInputRevision,
        Category = ToLegacy(opportunity.Status),
        DoNotResurface = opportunity.DoNotResurface,
        RepeatTalkOverride = opportunity.RepeatTalkOverride,
        RecordedSubmissionStatus = opportunity.RecordedSubmissionStatus,
    };

    public static OpportunityRecord Import(EventRecord record) => new()
    {
        Event = record with { ETag = null }, UserStatus = FromLegacy(record.Category),
        LegacyCategory = record.Category, PlacementSource = PlacementSource.Migration,
        DoNotResurface = record.DoNotResurface,
        SystemVerdict = !string.IsNullOrWhiteSpace(record.DecidedByAgent) && Enum.TryParse<Recommendation>(record.Category.ToString(), out var verdict) ? verdict : null,
    };
}
