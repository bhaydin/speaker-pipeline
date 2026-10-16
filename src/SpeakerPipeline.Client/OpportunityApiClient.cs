using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Client;

public sealed class OpportunityApiClient(HttpClient http) : IOpportunityApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    public async Task<IReadOnlyList<OpportunityRecord>> GetOpportunitiesAsync(CancellationToken ct = default)
        => await http.GetFromJsonAsync<IReadOnlyList<OpportunityRecord>>("v1/opportunities", Json, ct).ConfigureAwait(false) ?? [];
    public async Task<OpportunityRecord?> GetOpportunityAsync(string slug, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"v1/opportunities/{Uri.EscapeDataString(slug)}", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<OpportunityRecord>(Json, ct).ConfigureAwait(false);
    }
    public async Task<IReadOnlyList<OpportunityHistoryEntry>> GetOpportunityHistoryAsync(string slug, CancellationToken ct = default)
        => await http.GetFromJsonAsync<IReadOnlyList<OpportunityHistoryEntry>>($"v1/opportunities/{Uri.EscapeDataString(slug)}/history", Json, ct).ConfigureAwait(false) ?? [];
    public async Task<IReadOnlyList<OpportunityHistoryEntry>> GetOpportunityFeedbackAsync(int limit = 20, CancellationToken ct = default)
        => await http.GetFromJsonAsync<IReadOnlyList<OpportunityHistoryEntry>>($"v1/opportunities/feedback?limit={Math.Clamp(limit, 1, 100)}", Json, ct).ConfigureAwait(false) ?? [];
    public Task<OpportunityRecord> ApplyOpportunityActionAsync(string slug, OpportunityActionRequest request, string expectedETag, CancellationToken ct = default)
        => SendAsync($"v1/opportunities/{Uri.EscapeDataString(slug)}/actions", request, expectedETag, ct);
    public Task<OpportunityRecord> PostOpportunityDecisionAsync(OpportunityScoringRequest request, CancellationToken ct = default)
        => SendAsync($"v1/opportunities/{Uri.EscapeDataString(request.Decision.EventSlug)}/decisions", request, request.ExpectedETag, ct);
    public Task<OpportunityRecord> ReconcileOpportunitySubmissionsAsync(string slug, string expectedETag, CancellationToken ct = default)
        => SendAsync($"v1/opportunities/{Uri.EscapeDataString(slug)}/reconcile-submissions", new { }, expectedETag, ct);
    private async Task<OpportunityRecord> SendAsync<T>(string url, T body, string etag, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, options: Json) };
        message.Headers.TryAddWithoutValidation("If-Match", etag);
        using var response = await http.SendAsync(message, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            var conflict = await response.Content.ReadFromJsonAsync<ConflictResponse>(Json, ct).ConfigureAwait(false);
            throw new OpportunityConflictException(conflict?.Error ?? "Opportunity changed.", conflict?.Current, response.StatusCode == HttpStatusCode.Conflict);
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<OpportunityRecord>(Json, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("API returned an empty opportunity.");
    }
    private sealed record ConflictResponse(string? Error, OpportunityRecord? Current);
}
