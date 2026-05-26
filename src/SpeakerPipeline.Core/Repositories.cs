namespace SpeakerPipeline.Core;

/// <summary>
/// Repository abstractions over the persistence layer. Defined in Core so
/// they are visible to the API. Implemented in SpeakerPipeline.Storage.
/// Agents and external consumers do NOT depend on these — they go through
/// the REST API (<see cref="ISpeakerPipelineApiClient"/>).
/// </summary>
public interface IEventRepository
{
    Task<EventRecord?> GetAsync(string slug, CancellationToken ct = default);
    IAsyncEnumerable<EventRecord> QueryAsync(CancellationToken ct = default);
    Task<IReadOnlyList<EventRecord>> GetByCategoryAsync(IReadOnlyList<EventCategory> categories, CancellationToken ct = default);
    Task<IReadOnlyList<EventRecord>> GetUpcomingDeadlinesAsync(TimeSpan window, CancellationToken ct = default);
    Task UpsertAsync(EventRecord record, CancellationToken ct = default);
    Task DeleteAsync(string slug, CancellationToken ct = default);
}

public interface ISubmissionRepository
{
    Task<SubmissionRecord?> GetAsync(string eventSlug, string submissionId, CancellationToken ct = default);
    Task<IReadOnlyList<SubmissionRecord>> GetForEventAsync(string eventSlug, CancellationToken ct = default);
    Task UpsertAsync(SubmissionRecord record, CancellationToken ct = default);
}

public interface ITalkRepository
{
    Task<TalkRecord?> GetAsync(string slug, CancellationToken ct = default);
    Task<IReadOnlyList<TalkRecord>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TalkRecord>> GetByLaneAsync(Lane lane, CancellationToken ct = default);
    Task UpsertAsync(TalkRecord record, CancellationToken ct = default);
}
