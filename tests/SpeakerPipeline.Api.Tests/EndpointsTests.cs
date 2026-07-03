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

    [Fact]
    public async Task Post_topic_stamps_timestamps_and_round_trips()
    {
        var topic = new TopicRecord
        {
            TopicId = "model-access-supply-chain-risk",
            Title = "Model-access supply-chain risk",
            OneLiner = "Who can reach your model, and what they can do once they're there.",
            Stage = TopicStage.Idea,
            Source = TopicSource.Claude,
        };

        var postResp = await _fx.Client.PostAsJsonAsync("/v1/topics", topic);
        Assert.Equal(HttpStatusCode.Created, postResp.StatusCode);
        var created = await postResp.Content.ReadFromJsonAsync<TopicRecord>();
        Assert.NotNull(created);
        Assert.NotNull(created!.CreatedUtc);
        Assert.NotNull(created.UpdatedUtc);

        var getResp = await _fx.Client.GetAsync($"/v1/topics/{topic.TopicId}");
        var back = await getResp.Content.ReadFromJsonAsync<TopicRecord>();
        Assert.Equal(topic.Title, back!.Title);
        Assert.Equal(TopicSource.Claude, back.Source);
    }

    [Fact]
    public async Task Get_topics_filters_by_stage()
    {
        await _fx.Factory.Topics.UpsertAsync(new TopicRecord
        {
            TopicId = "validated-idea", Title = "Validated", Stage = TopicStage.Validated, Source = TopicSource.Manual,
        });
        await _fx.Factory.Topics.UpsertAsync(new TopicRecord
        {
            TopicId = "raw-idea", Title = "Raw", Stage = TopicStage.Idea, Source = TopicSource.Manual,
        });

        var resp = await _fx.Client.GetAsync("/v1/topics?stage=Validated");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var list = await resp.Content.ReadFromJsonAsync<List<TopicRecord>>();
        Assert.Contains(list!, t => t.TopicId == "validated-idea");
        Assert.DoesNotContain(list!, t => t.TopicId == "raw-idea");
    }

    [Fact]
    public async Task Post_blackout_with_end_before_start_returns_400()
    {
        var bad = new BlackoutRecord
        {
            BlackoutId = "inverted-range",
            StartDate = new DateTimeOffset(2027, 7, 20, 0, 0, 0, TimeSpan.Zero),
            EndDate = new DateTimeOffset(2027, 7, 10, 0, 0, 0, TimeSpan.Zero),
            Reason = "typo",
            Hardness = BlackoutHardness.Hard,
        };

        var resp = await _fx.Client.PostAsJsonAsync("/v1/blackouts", bad);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Post_blackout_round_trips()
    {
        var blackout = new BlackoutRecord
        {
            BlackoutId = "family-2027-07",
            StartDate = new DateTimeOffset(2027, 7, 10, 0, 0, 0, TimeSpan.Zero),
            EndDate = new DateTimeOffset(2027, 7, 20, 0, 0, 0, TimeSpan.Zero),
            Reason = "Family salmon trip",
            Hardness = BlackoutHardness.Hard,
        };

        var postResp = await _fx.Client.PostAsJsonAsync("/v1/blackouts", blackout);
        Assert.Equal(HttpStatusCode.Created, postResp.StatusCode);

        var getResp = await _fx.Client.GetAsync("/v1/blackouts");
        var list = await getResp.Content.ReadFromJsonAsync<List<BlackoutRecord>>();
        Assert.Contains(list!, b => b.BlackoutId == "family-2027-07" && b.Hardness == BlackoutHardness.Hard);
    }

    [Theory]
    [InlineData(PipelineAction.Skip, EventCategory.Skip, true)]
    [InlineData(PipelineAction.Monitor, EventCategory.Monitor, false)]
    [InlineData(PipelineAction.Intend, EventCategory.SubmitNow, false)]
    [InlineData(PipelineAction.Confirmed, EventCategory.Submitted, false)]
    public async Task Pipeline_action_moves_category(PipelineAction action, EventCategory expected, bool expectDoNotResurface)
    {
        var slug = $"pipeline-{action}".ToLowerInvariant();
        await _fx.Factory.Events.UpsertAsync(new EventRecord
        {
            Slug = slug,
            Name = "Pipeline Test",
            EventType = EventType.Conference,
            Category = EventCategory.Monitor,
            Priority = Priority.Medium,
        });

        var resp = await _fx.Client.PostAsJsonAsync($"/v1/pipeline/{slug}/actions",
            new PipelineActionRequest { Action = action, Note = "via test" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var updated = await _fx.Factory.Events.GetAsync(slug);
        Assert.Equal(expected, updated!.Category);
        Assert.Equal(expectDoNotResurface, updated.DoNotResurface);
        Assert.Equal("via test", updated.StatusDetail);
    }

    [Fact]
    public async Task Pipeline_action_on_missing_event_returns_404()
    {
        var resp = await _fx.Client.PostAsJsonAsync("/v1/pipeline/does-not-exist/actions",
            new PipelineActionRequest { Action = PipelineAction.Monitor });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
