using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Client;

/// <summary>
/// HTTP-backed implementation of <see cref="ISpeakerPipelineApiClient"/>.
/// Shared by every API consumer — the scoring agent, the MCP server, and any
/// future agent. Does NOT reference SpeakerPipeline.Storage; all access is
/// through the API. See ADR 0001.
/// </summary>
public sealed class SpeakerPipelineApiClient(HttpClient http, ILogger<SpeakerPipelineApiClient> logger) : ISpeakerPipelineApiClient
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<IReadOnlyList<EventRecord>> GetEventsAsync(
        IReadOnlyList<EventCategory>? categories = null,
        TimeSpan? deadlineWindow = null,
        CancellationToken ct = default)
    {
        var query = new List<string>();
        if (categories is { Count: > 0 })
        {
            query.Add($"category={Uri.EscapeDataString(string.Join(",", categories))}");
        }
        if (deadlineWindow is TimeSpan w)
        {
            query.Add($"deadlineDays={(int)w.TotalDays}");
        }

        var url = query.Count == 0 ? "v1/events" : $"v1/events?{string.Join("&", query)}";
        return await GetJsonAsync<IReadOnlyList<EventRecord>>(url, ct) ?? [];
    }

    public Task<EventRecord?> GetEventAsync(string slug, CancellationToken ct = default)
        => GetJsonAsync<EventRecord?>($"v1/events/{Uri.EscapeDataString(slug)}", ct, allow404: true);

    public async Task<EventRecord> UpsertEventAsync(EventRecord record, CancellationToken ct = default)
    {
        using var resp = await http.PutAsJsonAsync($"v1/events/{Uri.EscapeDataString(record.Slug)}", record, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<EventRecord>(JsonOpts, ct))!;
    }

    public async Task<IReadOnlyList<SubmissionRecord>> GetSubmissionsForEventAsync(string eventSlug, CancellationToken ct = default)
        => await GetJsonAsync<IReadOnlyList<SubmissionRecord>>($"v1/events/{Uri.EscapeDataString(eventSlug)}/submissions", ct) ?? [];

    public async Task<SubmissionRecord> UpsertSubmissionAsync(SubmissionRecord record, CancellationToken ct = default)
    {
        using var resp = await http.PutAsJsonAsync(
            $"v1/events/{Uri.EscapeDataString(record.EventSlug)}/submissions/{Uri.EscapeDataString(record.SubmissionId)}",
            record, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<SubmissionRecord>(JsonOpts, ct))!;
    }

    public async Task<IReadOnlyList<TalkRecord>> GetTalksAsync(Lane? lane = null, CancellationToken ct = default)
    {
        var url = lane is null ? "v1/talks" : $"v1/talks?lane={lane}";
        return await GetJsonAsync<IReadOnlyList<TalkRecord>>(url, ct) ?? [];
    }

    public Task<TalkRecord?> GetTalkAsync(string slug, CancellationToken ct = default)
        => GetJsonAsync<TalkRecord?>($"v1/talks/{Uri.EscapeDataString(slug)}", ct, allow404: true);

    public async Task<TalkRecord> UpsertTalkAsync(TalkRecord record, CancellationToken ct = default)
    {
        using var resp = await http.PutAsJsonAsync($"v1/talks/{Uri.EscapeDataString(record.Slug)}", record, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<TalkRecord>(JsonOpts, ct))!;
    }

    public async Task<IReadOnlyList<EventRecord>> GetScoringCandidatesAsync(CancellationToken ct = default)
        => await GetJsonAsync<IReadOnlyList<EventRecord>>("v1/scoring/candidates", ct) ?? [];

    public async Task PostScoringDecisionAsync(ScoringDecision decision, CancellationToken ct = default)
    {
        using var resp = await http.PostAsJsonAsync("v1/scoring/decisions", decision, JsonOpts, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            logger.LogWarning("Posting scoring decision for {Slug} returned {Status}: {Body}",
                decision.EventSlug, (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }
    }

    public async Task<PipelineContext> GetPipelineContextAsync(CancellationToken ct = default)
        => await GetJsonAsync<PipelineContext>("v1/pipeline/context", ct) ?? PipelineContext.Empty;

    public async Task<IReadOnlyList<TopicRecord>> GetTopicsAsync(TopicStage? stage = null, CancellationToken ct = default)
    {
        var url = stage is null ? "v1/topics" : $"v1/topics?stage={stage}";
        return await GetJsonAsync<IReadOnlyList<TopicRecord>>(url, ct) ?? [];
    }

    public Task<TopicRecord?> GetTopicAsync(string topicId, CancellationToken ct = default)
        => GetJsonAsync<TopicRecord?>($"v1/topics/{Uri.EscapeDataString(topicId)}", ct, allow404: true);

    public async Task<TopicRecord> UpsertTopicAsync(TopicRecord record, CancellationToken ct = default)
    {
        using var resp = await http.PutAsJsonAsync($"v1/topics/{Uri.EscapeDataString(record.TopicId)}", record, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<TopicRecord>(JsonOpts, ct))!;
    }

    public async Task<IReadOnlyList<BlackoutRecord>> GetBlackoutsAsync(CancellationToken ct = default)
        => await GetJsonAsync<IReadOnlyList<BlackoutRecord>>("v1/blackouts", ct) ?? [];

    public async Task<BlackoutRecord> UpsertBlackoutAsync(BlackoutRecord record, CancellationToken ct = default)
    {
        using var resp = await http.PutAsJsonAsync($"v1/blackouts/{Uri.EscapeDataString(record.BlackoutId)}", record, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<BlackoutRecord>(JsonOpts, ct))!;
    }

    public async Task<EventRecord> ApplyPipelineActionAsync(string slug, PipelineActionRequest request, CancellationToken ct = default)
    {
        using var resp = await http.PostAsJsonAsync($"v1/pipeline/{Uri.EscapeDataString(slug)}/actions", request, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<EventRecord>(JsonOpts, ct))!;
    }

    public async Task<IReadOnlyList<NotificationLogRecord>> GetNotificationsAsync(string period, CancellationToken ct = default)
        => await GetJsonAsync<IReadOnlyList<NotificationLogRecord>>($"v1/notifications/{Uri.EscapeDataString(period)}", ct) ?? [];

    public async Task<NotificationLogRecord> LogNotificationAsync(NotificationLogRecord record, CancellationToken ct = default)
    {
        using var resp = await http.PostAsJsonAsync("v1/notifications", record, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<NotificationLogRecord>(JsonOpts, ct))!;
    }

    private async Task<T?> GetJsonAsync<T>(string path, CancellationToken ct, bool allow404 = false)
    {
        using var resp = await http.GetAsync(path, ct);
        if (allow404 && resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return default;
        }
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<T>(JsonOpts, ct);
    }
}
