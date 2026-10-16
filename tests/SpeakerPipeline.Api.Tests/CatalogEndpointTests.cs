using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Api.Tests;

public sealed class CatalogEndpointTests
{
    private const string Secret = "local-test-webhook-secret";
    private const string Payload = """{"ref":"refs/heads/main","repository":{"full_name":"bhaydin/bhaydin-talks","default_branch":"main"}}""";
    private static string Sign(string body) => "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(body)));

    [Fact]
    public async Task Signed_webhook_queues_once_and_tampering_is_rejected()
    {
        using var baseFactory = new TestWebApplicationFactory();
        var repository = new MemoryCatalog();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Catalog:GitHub:WebhookSecret"] = Secret }));
            builder.ConfigureServices(services => { services.RemoveAll<ICatalogRepository>(); services.AddSingleton<ICatalogRepository>(repository); });
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "dev");
        var request = new CatalogWebhookRequest("delivery-1", "push", Sign(Payload), Payload);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/v1/catalog/webhook", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/v1/catalog/webhook", request)).StatusCode);
        Assert.Single(repository.Jobs);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/v1/catalog/webhook", request with { Payload = Payload.Replace("main", "evil", StringComparison.Ordinal) })).StatusCode);
        Assert.Single(repository.Jobs);
        var otherBranch = Payload.Replace("refs/heads/main", "refs/heads/draft", StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/v1/catalog/webhook", request with { DeliveryId = "delivery-2", Payload = otherBranch, Signature = Sign(otherBranch) })).StatusCode);
        Assert.Single(repository.Jobs);
        var renamedDefault = Payload.Replace("main", "trunk", StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/v1/catalog/webhook", request with { DeliveryId = "delivery-3", Payload = renamedDefault, Signature = Sign(renamedDefault) })).StatusCode);
        Assert.Equal(2, repository.Jobs.Count);
    }

    [Fact]
    public async Task Missing_webhook_secret_fails_closed_and_catalog_requires_authentication()
    {
        using var baseFactory = new TestWebApplicationFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        { services.RemoveAll<ICatalogRepository>(); services.AddSingleton<ICatalogRepository>(new MemoryCatalog()); }));
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/catalog")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "dev");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/v1/catalog/webhook", new CatalogWebhookRequest("delivery", "push", Sign(Payload), Payload))).StatusCode);
    }

    private sealed class MemoryCatalog : ICatalogRepository
    {
        public HashSet<string> Jobs { get; } = [];
        public Task<CatalogSnapshot> GetAsync(CancellationToken ct = default) => Task.FromResult(new CatalogSnapshot());
        public Task<bool> EnqueueAsync(CatalogSyncRequest request, CancellationToken ct = default) => Task.FromResult(Jobs.Add(request.DeliveryId));
        public Task<CatalogSyncJob?> ClaimAsync(CancellationToken ct = default) => Task.FromResult<CatalogSyncJob?>(null);
        public Task CompleteAsync(string id, CatalogJobCompletion request, CancellationToken ct = default) => Task.CompletedTask;
        public Task PublishAsync(CatalogPublishRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public Task<CatalogExtraction?> GetExtractionAsync(string hash, CancellationToken ct = default) => Task.FromResult<CatalogExtraction?>(null);
        public Task SaveExtractionAsync(CatalogExtraction extraction, CancellationToken ct = default) => Task.CompletedTask;
    }
}
