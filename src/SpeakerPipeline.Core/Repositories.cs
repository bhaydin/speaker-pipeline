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

public interface ITopicRepository
{
    Task<TopicRecord?> GetAsync(string topicId, CancellationToken ct = default);
    Task<IReadOnlyList<TopicRecord>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TopicRecord>> GetByStageAsync(TopicStage stage, CancellationToken ct = default);
    Task UpsertAsync(TopicRecord record, CancellationToken ct = default);
}

public interface IBlackoutRepository
{
    Task<BlackoutRecord?> GetAsync(string blackoutId, CancellationToken ct = default);
    Task<IReadOnlyList<BlackoutRecord>> GetAllAsync(CancellationToken ct = default);
    Task UpsertAsync(BlackoutRecord record, CancellationToken ct = default);
}

/// <summary>
/// Persistence for the notification dedupe ledger. Defined in Milestone 1; its
/// first writer (the Notifier) and API endpoints arrive in Milestone 2.
/// See <see cref="NotificationLogRecord"/> and ADR 0001.
/// </summary>
public interface INotificationLogRepository
{
    Task<NotificationLogRecord?> GetAsync(string period, string notificationId, CancellationToken ct = default);
    Task<IReadOnlyList<NotificationLogRecord>> GetForPeriodAsync(string period, CancellationToken ct = default);
    Task UpsertAsync(NotificationLogRecord record, CancellationToken ct = default);
}
