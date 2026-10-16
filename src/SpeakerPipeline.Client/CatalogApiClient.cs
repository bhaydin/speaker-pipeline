using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Client;

public sealed class CatalogApiClient(HttpClient http) : ICatalogApiClient
{
    public async Task<CatalogSnapshot> GetCatalogAsync(CancellationToken ct = default)
        => await http.GetFromJsonAsync<CatalogSnapshot>("v1/catalog", ct) ?? new CatalogSnapshot();
    public async Task<bool> EnqueueCatalogSyncAsync(CatalogSyncRequest request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("v1/catalog/sync", request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<bool>(ct);
    }
    public async Task<CatalogSyncJob?> ClaimCatalogSyncAsync(CancellationToken ct = default)
    {
        using var response = await http.PostAsync("v1/catalog/jobs/claim", null, ct);
        response.EnsureSuccessStatusCode();
        return response.StatusCode == HttpStatusCode.NoContent ? null : await response.Content.ReadFromJsonAsync<CatalogSyncJob>(ct);
    }
    public async Task CompleteCatalogSyncAsync(string jobId, CatalogJobCompletion request, CancellationToken ct = default)
    { using var response = await http.PostAsJsonAsync($"v1/catalog/jobs/{Uri.EscapeDataString(jobId)}/complete", request, ct); response.EnsureSuccessStatusCode(); }
    public async Task PublishCatalogAsync(CatalogPublishRequest request, CancellationToken ct = default)
    { using var response = await http.PutAsJsonAsync("v1/catalog", request, ct); response.EnsureSuccessStatusCode(); }
    public async Task<CatalogExtraction?> GetCatalogExtractionAsync(string hash, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"v1/catalog/extractions/{Uri.EscapeDataString(hash)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CatalogExtraction>(ct);
    }
    public async Task SaveCatalogExtractionAsync(CatalogExtraction extraction, CancellationToken ct = default)
    { using var response = await http.PutAsJsonAsync("v1/catalog/extractions", extraction, ct); response.EnsureSuccessStatusCode(); }
    public async Task AcceptCatalogWebhookAsync(CatalogWebhookRequest request, CancellationToken ct = default)
    { using var response = await http.PostAsJsonAsync("v1/catalog/webhook", request, ct); response.EnsureSuccessStatusCode(); }
    public async Task<CatalogSnapshot> ReviewCatalogAsync(CatalogReviewRequest request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("v1/catalog/review", request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CatalogSnapshot>(ct))!;
    }
}

public static class CatalogClientServiceCollectionExtensions
{
    public static IHttpClientBuilder AddCatalogApiClient(this IServiceCollection services, IConfiguration configuration)
        => services.AddHttpClient<ICatalogApiClient, CatalogApiClient>(http =>
        {
            if (Uri.TryCreate(configuration["SpeakerPipelineApi:BaseUrl"], UriKind.Absolute, out var uri)) http.BaseAddress = uri;
            http.Timeout = TimeSpan.FromMinutes(2);
        });
}
