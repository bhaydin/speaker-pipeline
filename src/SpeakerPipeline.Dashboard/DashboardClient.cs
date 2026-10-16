using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Dashboard;

public sealed record DashboardSession(bool Authenticated, string? Name, string? RequestToken, string? LoginUrl);

public sealed class DashboardClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
    private string? _requestToken;

    public async Task<DashboardSession> GetSessionAsync()
    {
        var session = await http.GetFromJsonAsync<DashboardSession>("/v1/dashboard/session", Json)
            ?? throw new InvalidOperationException("The sign-in session could not be loaded.");
        _requestToken = session.RequestToken;
        return session;
    }

    public async Task<IReadOnlyList<OpportunityRecord>> GetOpportunitiesAsync()
        => await http.GetFromJsonAsync<List<OpportunityRecord>>("/v1/opportunities", Json) ?? [];
    public async Task<OpportunityRecord?> GetOpportunityAsync(string slug)
        => await http.GetFromJsonAsync<OpportunityRecord>($"/v1/opportunities/{Uri.EscapeDataString(slug)}", Json);
    public async Task<IReadOnlyList<OpportunityHistoryEntry>> GetHistoryAsync(string slug)
        => await http.GetFromJsonAsync<List<OpportunityHistoryEntry>>($"/v1/opportunities/{Uri.EscapeDataString(slug)}/history", Json) ?? [];
    public async Task<CatalogSnapshot> GetCatalogAsync()
        => await http.GetFromJsonAsync<CatalogSnapshot>("/v1/catalog", Json) ?? new();

    public async Task<IReadOnlyList<TalkRecord>> GetLegacyTalksAsync()
        => await http.GetFromJsonAsync<List<TalkRecord>>("/v1/talks", Json) ?? [];

    public async Task<CatalogSnapshot> ReviewCatalogAsync(CatalogReviewRequest review)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/catalog/review")
        { Content = JsonContent.Create(review, options: Json) };
        request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", _requestToken);
        using var response = await http.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
            throw new DashboardConflictException("The catalog changed. Review the refreshed version before saving again.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CatalogSnapshot>(Json)
            ?? throw new InvalidOperationException("The updated catalog could not be loaded.");
    }

    public async Task<IReadOnlyList<AgentRunRecord>> GetRunsAsync()
    {
        var documents = await http.GetFromJsonAsync<List<VersionedOperation>>("/v1/operations/runs", Json) ?? [];
        return documents.Select(d => JsonSerializer.Deserialize<AgentRunRecord>(d.Document.Payload, Json))
            .OfType<AgentRunRecord>().ToArray();
    }

    public async Task<OpportunityRecord> ActAsync(OpportunityRecord opportunity, OpportunityActionRequest action)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/opportunities/{Uri.EscapeDataString(opportunity.Slug)}/actions")
        { Content = JsonContent.Create(action, options: Json) };
        request.Headers.TryAddWithoutValidation("If-Match", opportunity.ETag);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", action.IdempotencyKey);
        request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", _requestToken);
        using var response = await http.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
            throw new DashboardConflictException("This opportunity changed elsewhere. Its latest state has been loaded; review it before trying again.");
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("Your session has expired or access was denied. Sign in again.");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(response.StatusCode == HttpStatusCode.BadRequest
                ? "Choose a reason for this change and check the fields before trying again."
                : "The change could not be saved. Your current state is preserved.");
        return await response.Content.ReadFromJsonAsync<OpportunityRecord>(Json)
            ?? throw new InvalidOperationException("The API did not return the updated opportunity.");
    }

    public async Task SignOutAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/dashboard/logout");
        request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", _requestToken);
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }
}

public sealed class DashboardConflictException(string message) : Exception(message);
