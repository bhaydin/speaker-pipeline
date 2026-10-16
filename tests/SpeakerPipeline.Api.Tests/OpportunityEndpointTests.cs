using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Api.Tests;

public sealed class OpportunityEndpointTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    [Fact]
    public async Task Feature_gate_and_auth_prevent_premature_access()
    {
        await using var factory = new TestWebApplicationFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/opportunities")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "dev");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/v1/opportunities")).StatusCode);
    }

    [Fact]
    public async Task Exact_etag_is_required_and_conflicts_return_current_state()
    {
        var repository = new ContractRepository();
        await using var baseFactory = new TestWebApplicationFactory();
        await using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:OpportunitiesEnabled"] = "true" }));
            builder.ConfigureServices(services => { services.RemoveAll<IOpportunityRepository>(); services.AddSingleton<IOpportunityRepository>(repository); });
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "dev");
        var detail = await client.GetAsync("/v1/opportunities/fictional");
        Assert.Equal("\"revision-1\"", detail.Headers.ETag!.ToString());
        var action = new OpportunityActionRequest { Action = OpportunityAction.Submitted, IdempotencyKey = "button-1" };
        Assert.Equal((HttpStatusCode)428, (await client.PostAsJsonAsync("/v1/opportunities/fictional/actions", action, Json)).StatusCode);
        Assert.Equal(0, repository.Calls);
        using var stale = new HttpRequestMessage(HttpMethod.Post, "/v1/opportunities/fictional/actions") { Content = JsonContent.Create(action, options: Json) };
        stale.Headers.IfMatch.ParseAdd("\"revision-0\"");
        var rejected = await client.SendAsync(stale);
        Assert.Equal(HttpStatusCode.PreconditionFailed, rejected.StatusCode);
        Assert.Contains("revision-1", await rejected.Content.ReadAsStringAsync());
        using var fresh = new HttpRequestMessage(HttpMethod.Post, "/v1/opportunities/fictional/actions") { Content = JsonContent.Create(action, options: Json) };
        fresh.Headers.IfMatch.ParseAdd("\"revision-1\"");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(fresh)).StatusCode);
        Assert.Equal(2, repository.Calls);
    }

    [Theory]
    [InlineData(null, null, 428)]
    [InlineData(" ", null, 428)]
    [InlineData("body-key", "different-header-key", 400)]
    public async Task User_action_requires_consistent_idempotency_key(string? bodyKey, string? headerKey, int status)
    {
        var repository = new ContractRepository();
        await using var baseFactory = new TestWebApplicationFactory();
        await using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:OpportunitiesEnabled"] = "true" }));
            builder.ConfigureServices(services => { services.RemoveAll<IOpportunityRepository>(); services.AddSingleton<IOpportunityRepository>(repository); });
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "dev");
        var body = new Dictionary<string, string> { ["action"] = "Submitted" };
        if (bodyKey is not null) body["idempotencyKey"] = bodyKey;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/opportunities/fictional/actions") { Content = JsonContent.Create(body) };
        request.Headers.IfMatch.ParseAdd("\"revision-1\"");
        if (headerKey is not null) request.Headers.Add("Idempotency-Key", headerKey);
        Assert.Equal(status, (int)(await client.SendAsync(request)).StatusCode);
        Assert.Equal(0, repository.Calls);
    }

    [Fact]
    public async Task Catalog_decisions_validate_published_generation_versions_and_repeat_override()
    {
        var repository = new ContractRepository();
        var catalog = new ContractCatalog();
        await using var baseFactory = new TestWebApplicationFactory();
        await using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            { ["Storage:OpportunitiesEnabled"] = "true", ["Catalog:Enabled"] = "true" }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IOpportunityRepository>(); services.AddSingleton<IOpportunityRepository>(repository);
                services.RemoveAll<ICatalogRepository>(); services.AddSingleton<ICatalogRepository>(catalog);
            });
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "dev");
        var request = new OpportunityScoringRequest
        {
            ExpectedETag = "\"revision-1\"", ScoringInputRevision = 1, IdempotencyKey = "decision-1",
            Decision = new() { EventSlug = "fictional", Recommendation = Recommendation.SubmitNow, Rationale = "Direct fit", FitScore = 8, EffortScore = 3, ConfidenceScore = 8,
                InputRevision = "1", CatalogGenerationId = "old-published", MatchedTalks = [new("talk", "version-1", "Canonical title", "Fits the track")] },
        };
        var url = "/v1/opportunities/fictional/decisions";
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(url, request, Json)).StatusCode); // current catalog may already have advanced
        Assert.Equal(1, repository.ScoreCalls);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, request with { Decision = request.Decision with { CatalogGenerationId = "unpublished" } }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, request with { Decision = request.Decision with { MatchedTalks = [new("talk", "invented-version", "Canonical title", "Fits")] } }, Json)).StatusCode);
        catalog.HasCompletedDelivery = true;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, request, Json)).StatusCode);
        repository.RepeatOverride = true;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(url, request, Json)).StatusCode);
        Assert.Equal(2, repository.ScoreCalls);
    }

    [Fact]
    public async Task Legacy_actions_require_explicit_drop_reason_and_do_not_invent_monitor_feedback()
    {
        var repository = new ContractRepository();
        await using var baseFactory = new TestWebApplicationFactory();
        await using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:OpportunitiesEnabled"] = "true" }));
            builder.ConfigureServices(services => { services.RemoveAll<IOpportunityRepository>(); services.AddSingleton<IOpportunityRepository>(repository); });
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "dev");
        async Task<HttpStatusCode> Send(PipelineActionRequest action)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/pipeline/fictional/actions") { Content = JsonContent.Create(action, options: Json) };
            request.Headers.IfMatch.ParseAdd("\"revision-1\"");
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("n"));
            return (await client.SendAsync(request)).StatusCode;
        }
        Assert.Equal(HttpStatusCode.BadRequest, await Send(new() { Action = PipelineAction.Skip }));
        Assert.Equal(HttpStatusCode.OK, await Send(new() { Action = PipelineAction.Skip, ReasonCode = FeedbackReason.TooFar }));
        Assert.Equal(FeedbackReason.TooFar, repository.LastAction!.ReasonCode);
        Assert.Equal(HttpStatusCode.OK, await Send(new() { Action = PipelineAction.Monitor }));
        Assert.Null(repository.LastAction!.ReasonCode);
    }

    private sealed class ContractRepository : IOpportunityRepository
    {
        public int Calls { get; private set; }
        public int ScoreCalls { get; private set; }
        public bool RepeatOverride { get; set; }
        public OpportunityActionRequest? LastAction { get; private set; }
        private static readonly OpportunityRecord Current = new()
        {
            ETag = "\"revision-1\"", Event = new() { Slug = "fictional", Name = "Fictional event", EventType = EventType.Conference, Category = EventCategory.Monitor, Priority = Priority.Low, CfpUrl = "https://example.invalid/cfp", CfpStatus = CfpStatus.Open },
        };
        public Task<OpportunityRecord?> GetAsync(string slug, CancellationToken ct = default) => Task.FromResult<OpportunityRecord?>(Current with { RepeatTalkOverride = RepeatOverride });
        public Task<IReadOnlyList<OpportunityRecord>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<OpportunityRecord>>([Current]);
        public Task<IReadOnlyList<OpportunityHistoryEntry>> GetHistoryAsync(string slug, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<OpportunityHistoryEntry>>([]);
        public Task<IReadOnlyList<OpportunityHistoryEntry>> GetFeedbackAsync(int limit = 20, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<OpportunityHistoryEntry>>([]);
        public Task<OpportunityRecord> ApplyActionAsync(string slug, OpportunityActionRequest request, string expectedETag, string actor, CancellationToken ct = default)
        {
            Calls++;
            LastAction = request;
            if (expectedETag != Current.ETag) throw new OpportunityConflictException("Refresh required", Current);
            return Task.FromResult(OpportunityTransitions.Apply(Current, request, DateTimeOffset.UtcNow));
        }
        public Task<OpportunityRecord> ApplyDecisionAsync(OpportunityScoringRequest request, CancellationToken ct = default) { ScoreCalls++; return Task.FromResult(Current); }
        public Task<OpportunityRecord> ApplyDecisionAsync(OpportunityScoringRequest request, CatalogSnapshot? catalog, CancellationToken ct = default)
        {
            OpportunityDecisionPolicy.Validate(Current with { RepeatTalkOverride = RepeatOverride }, request, DateTimeOffset.UtcNow, catalog);
            return ApplyDecisionAsync(request, ct);
        }
        public Task<OpportunityRecord> UpsertFactsAsync(EventRecord record, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class ContractCatalog : ICatalogRepository
    {
        private static readonly CatalogSource Source = new("repo", "talk.yaml", "commit", "hash");
        public bool HasCompletedDelivery { get; set; }
        public Task<CatalogSnapshot> GetAsync(CancellationToken ct = default) => Task.FromResult(new CatalogSnapshot { GenerationId = "new-generation" });
        public Task<CatalogSnapshot?> GetGenerationAsync(string generationId, CancellationToken ct = default) => Task.FromResult<CatalogSnapshot?>(generationId == "old-published" ? new()
        {
            GenerationId = generationId,
            Talks = [new() { TalkId = "talk", CurrentVersionId = "version-1", Title = "Canonical title", Source = Source }],
            Versions = [new() { TalkId = "talk", VersionId = "version-1", Title = "Canonical title", Source = Source }],
            Deliveries = HasCompletedDelivery ? [new() { DeliveryId = "completed", TalkId = "talk", EventName = "Fictional event 2025", Status = "completed", Source = Source }] : [],
        } : null);
        public Task<bool> EnqueueAsync(CatalogSyncRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CatalogSyncJob?> ClaimAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task CompleteAsync(string id, CatalogJobCompletion request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task PublishAsync(CatalogPublishRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CatalogExtraction?> GetExtractionAsync(string hash, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SaveExtractionAsync(CatalogExtraction extraction, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
