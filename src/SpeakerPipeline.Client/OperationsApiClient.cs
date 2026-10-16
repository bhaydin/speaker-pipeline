using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Client;

public static class OperationsClientRegistration
{
    public static IHttpClientBuilder AddPipelineOperationsClient(this IServiceCollection services, IConfiguration configuration)
        => services.AddHttpClient<IOperationsApiClient, OperationsApiClient>(client =>
        {
            client.BaseAddress = new Uri(configuration["SpeakerPipelineApi:BaseUrl"] ?? "http://localhost:5000");
            client.Timeout = TimeSpan.FromSeconds(30);
        });
}

public sealed class OperationsApiClient(HttpClient http) : IOperationsApiClient
{
    public async Task<VersionedOperation?> GetAsync(string kind, string id, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(Path(kind, id), ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<VersionedOperation>(ct);
    }

    public async Task<VersionedOperation?> SaveAsync(OperationDocument document, string? expectedVersion = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Path(document.Kind, document.Id)) { Content = JsonContent.Create(document) };
        if (expectedVersion is not null) request.Headers.TryAddWithoutValidation("If-Match", expectedVersion);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Conflict) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<VersionedOperation>(ct);
    }

    public async Task<IReadOnlyList<VersionedOperation>> ListAsync(string kind, DateTimeOffset since, CancellationToken ct = default)
        => await http.GetFromJsonAsync<VersionedOperation[]>(
            $"v1/operations/{Uri.EscapeDataString(kind)}?since={Uri.EscapeDataString(since.ToString("O"))}", ct) ?? [];

    private static string Path(string kind, string id) => $"v1/operations/{Uri.EscapeDataString(kind)}/{Uri.EscapeDataString(id)}";
}
