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

    /// <summary>
    /// Returns events that are due for scoring — those without a recent
    /// decision or with a deadline inside the next 60 days.
    /// </summary>
    Task<IReadOnlyList<EventRecord>> GetScoringCandidatesAsync(CancellationToken ct = default);

    Task PostScoringDecisionAsync(ScoringDecision decision, CancellationToken ct = default);
}
