namespace SpeakerPipeline.Core;

/// <summary>
/// The HTTP-shaped contract that agents and external consumers use to read
/// and write pipeline data. Concrete implementation is injected into agent
/// hosts; agents never reference SpeakerPipeline.Storage directly.
/// </summary>
public interface ISpeakerPipelineApiClient
{
    Task<IReadOnlyList<EventRecord>> GetEventsAsync(
        IReadOnlyList<EventCategory>? categories = null,
        TimeSpan? deadlineWindow = null,
        CancellationToken ct = default);

    Task<EventRecord?> GetEventAsync(string slug, CancellationToken ct = default);

    Task<EventRecord> UpsertEventAsync(EventRecord record, CancellationToken ct = default);

    Task<IReadOnlyList<SubmissionRecord>> GetSubmissionsForEventAsync(string eventSlug, CancellationToken ct = default);

    Task<SubmissionRecord> UpsertSubmissionAsync(SubmissionRecord record, CancellationToken ct = default);

    Task<IReadOnlyList<TalkRecord>> GetTalksAsync(Lane? lane = null, CancellationToken ct = default);

    Task<TalkRecord?> GetTalkAsync(string slug, CancellationToken ct = default);

    Task<TalkRecord> UpsertTalkAsync(TalkRecord record, CancellationToken ct = default);

    /// <summary>
    /// Returns events that are due for scoring — those without a recent
    /// decision or with a deadline inside the next 60 days.
    /// </summary>
    Task<IReadOnlyList<EventRecord>> GetScoringCandidatesAsync(CancellationToken ct = default);

    Task PostScoringDecisionAsync(ScoringDecision decision, CancellationToken ct = default);

    /// <summary>
    /// Assembles the calendar/load context the scoring agent windows per
    /// candidate: committed engagements, blackout ranges, and new-topic prep
    /// counts. One query set per scoring run.
    /// </summary>
    Task<PipelineContext> GetPipelineContextAsync(CancellationToken ct = default);

    // ---- Topics ----------------------------------------------------------

    Task<IReadOnlyList<TopicRecord>> GetTopicsAsync(TopicStage? stage = null, CancellationToken ct = default);

    Task<TopicRecord?> GetTopicAsync(string topicId, CancellationToken ct = default);

    Task<TopicRecord> UpsertTopicAsync(TopicRecord record, CancellationToken ct = default);

    // ---- Blackouts -------------------------------------------------------

    Task<IReadOnlyList<BlackoutRecord>> GetBlackoutsAsync(CancellationToken ct = default);

    Task<BlackoutRecord> UpsertBlackoutAsync(BlackoutRecord record, CancellationToken ct = default);

    // ---- Pipeline actions ------------------------------------------------

    /// <summary>
    /// Applies an explicit pipeline transition to an event. The closed action
    /// set enforces the intent-vs-confirmation ambiguity guard.
    /// </summary>
    Task<EventRecord> ApplyPipelineActionAsync(string slug, PipelineActionRequest request, CancellationToken ct = default);

    // ---- Notifications ---------------------------------------------------

    /// <summary>All notifications logged in a period ("yyyy-MM") — the dedup + digest feed.</summary>
    Task<IReadOnlyList<NotificationLogRecord>> GetNotificationsAsync(string period, CancellationToken ct = default);

    /// <summary>Records a sent notification (idempotent on Period + NotificationId).</summary>
    Task<NotificationLogRecord> LogNotificationAsync(NotificationLogRecord record, CancellationToken ct = default);
}
