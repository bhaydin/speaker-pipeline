using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Api.Tests;

public class EndpointsTests : IClassFixture<EndpointsTests.Fixture>
{
    private readonly Fixture _fx;

    public EndpointsTests(Fixture fx) => _fx = fx;

    public sealed class Fixture : IDisposable
    {
        public TestWebApplicationFactory Factory { get; } = new();
        public HttpClient Client { get; }

        public Fixture()
        {
            Client = Factory.CreateClient();
            Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "dev-token");
        }

        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
        }
    }

    [Fact]
    public async Task Health_returns_200_without_auth()
    {
        using var anonClient = _fx.Factory.CreateClient();
        var resp = await anonClient.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task GetEvents_requires_auth()
    {
        using var anon = _fx.Factory.CreateClient();
        var resp = await anon.GetAsync("/v1/events");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Post_then_get_event_round_trips()
    {
        var record = new EventRecord
        {
            Slug = "headwaters-codecamp-2027",
            Name = "Headwaters CodeCamp 2027",
            EventType = EventType.CodeCamp,
            Category = EventCategory.SubmitNow,
            Priority = Priority.MediumHigh,
        };

        var postResp = await _fx.Client.PostAsJsonAsync("/v1/events", record);
        Assert.Equal(HttpStatusCode.Created, postResp.StatusCode);

        var getResp = await _fx.Client.GetAsync($"/v1/events/{record.Slug}");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var back = await getResp.Content.ReadFromJsonAsync<EventRecord>();
        Assert.NotNull(back);
        Assert.Equal(record.Name, back!.Name);
    }

    [Fact]
    public async Task Post_event_with_invalid_slug_returns_400()
    {
        var bad = new EventRecord
        {
            Slug = "has/slash",
            Name = "Bad",
            EventType = EventType.Conference,
            Category = EventCategory.Monitor,
            Priority = Priority.Low,
        };
        var resp = await _fx.Client.PostAsJsonAsync("/v1/events", bad);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PostScoringDecision_updates_event_category()
    {
        var slug = "driftless-ai-days-2027";
        await _fx.Factory.Events.UpsertAsync(new EventRecord
        {
            Slug = slug,
            Name = "Driftless AI Days 2027",
            EventType = EventType.Conference,
            Category = EventCategory.Monitor,
            Priority = Priority.Medium,
        });

        var decision = new ScoringDecision
        {
            EventSlug = slug,
            Recommendation = Recommendation.SubmitNow,
            Rationale = "Strong fit for the Foundry enterprise lane and travel is low.",
            FitScore = 9,
            EffortScore = 4,
            ConfidenceScore = 8,
            DecidedByAgent = "scoring-agent-test",
        };

        var resp = await _fx.Client.PostAsJsonAsync("/v1/scoring/decisions", decision);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var updated = await _fx.Factory.Events.GetAsync(slug);
        Assert.NotNull(updated);
        Assert.Equal(EventCategory.SubmitNow, updated!.Category);
        Assert.Equal(Priority.High, updated.Priority);
        Assert.Equal("scoring-agent-test", updated.DecidedByAgent);
    }
}
