using SpeakerPipeline.Core;

namespace SpeakerPipeline.Telegram.Tests;

/// <summary>
/// In-memory API client that records the writes the command router makes, and
/// serves events for /status. Unknown slugs throw from ApplyPipelineActionAsync
/// to exercise the router's friendly-error path.
/// </summary>
internal sealed class FakeApiClient : ISpeakerPipelineApiClient
{
    public Dictionary<string, EventRecord> Events { get; } = new();
    public List<TopicRecord> UpsertedTopics { get; } = [];
    public List<(string Slug, PipelineActionRequest Request)> Actions { get; } = [];

    public Task<IReadOnlyList<EventRecord>> GetEventsAsync(IReadOnlyList<EventCategory>? categories = null, TimeSpan? deadlineWindow = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<EventRecord>>([.. Events.Values]);

    public Task<TopicRecord> UpsertTopicAsync(TopicRecord record, CancellationToken ct = default)
    {
        UpsertedTopics.Add(record);
        return Task.FromResult(record);
    }

    public Task<EventRecord> ApplyPipelineActionAsync(string slug, PipelineActionRequest request, CancellationToken ct = default)
    {
        Actions.Add((slug, request));
        if (!Events.TryGetValue(slug, out var e))
        {
            throw new InvalidOperationException($"No event '{slug}'.");
        }
        return Task.FromResult(e);
    }

    // Unused by the router — stubbed.
    public Task<EventRecord?> GetEventAsync(string slug, CancellationToken ct = default) => Task.FromResult<EventRecord?>(Events.GetValueOrDefault(slug));
    public Task<EventRecord> UpsertEventAsync(EventRecord record, CancellationToken ct = default) => Task.FromResult(record);
    public Task<IReadOnlyList<SubmissionRecord>> GetSubmissionsForEventAsync(string eventSlug, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SubmissionRecord>>([]);
    public Task<SubmissionRecord> UpsertSubmissionAsync(SubmissionRecord record, CancellationToken ct = default) => Task.FromResult(record);
    public Task<IReadOnlyList<TalkRecord>> GetTalksAsync(Lane? lane = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TalkRecord>>([]);
    public Task<TalkRecord?> GetTalkAsync(string slug, CancellationToken ct = default) => Task.FromResult<TalkRecord?>(null);
    public Task<TalkRecord> UpsertTalkAsync(TalkRecord record, CancellationToken ct = default) => Task.FromResult(record);
    public Task<IReadOnlyList<EventRecord>> GetScoringCandidatesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<EventRecord>>([]);
    public Task PostScoringDecisionAsync(ScoringDecision decision, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<TopicRecord>> GetTopicsAsync(TopicStage? stage = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TopicRecord>>([]);
    public Task<TopicRecord?> GetTopicAsync(string topicId, CancellationToken ct = default) => Task.FromResult<TopicRecord?>(null);
    public Task<IReadOnlyList<BlackoutRecord>> GetBlackoutsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<BlackoutRecord>>([]);
    public Task<BlackoutRecord> UpsertBlackoutAsync(BlackoutRecord record, CancellationToken ct = default) => Task.FromResult(record);
    public Task<IReadOnlyList<NotificationLogRecord>> GetNotificationsAsync(string period, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<NotificationLogRecord>>([]);
    public Task<NotificationLogRecord> LogNotificationAsync(NotificationLogRecord record, CancellationToken ct = default) => Task.FromResult(record);
}
