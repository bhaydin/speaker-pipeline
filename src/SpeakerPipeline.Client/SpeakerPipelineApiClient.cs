using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Agents.Scoring.ApiClient;

/// <summary>
/// HTTP-backed implementation of <see cref="ISpeakerPipelineApiClient"/>.
/// Used by the scoring agent and any other consumer that needs to read
/// pipeline data. Does NOT reference SpeakerPipeline.Storage.
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
