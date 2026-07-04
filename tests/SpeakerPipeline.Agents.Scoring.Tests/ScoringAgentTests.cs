using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Agents.Scoring;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Agents.Scoring.Tests;

public class ScoringAgentTests
{
    private static readonly EventRecord SampleEvent = new()
    {
        Slug = "northwoods-tech-summit-2027",
        Name = "Northwoods Tech Summit 2027",
        EventType = EventType.Conference,
        Category = EventCategory.SubmitNow,
        Priority = Priority.High,
        FocusFit = [Lane.AgentOps, Lane.HybridAgents],
        Location = "Minocqua, WI",
        Format = EventFormat.InPerson,
        TravelBurden = TravelBurden.Low,
    };

    private static readonly TalkRecord SampleTalk = new()
    {
        Slug = "agentops-real-world",
        CanonicalTitle = "AgentOps in the Real World",
        Lane = Lane.AgentOps,
        ReusabilityScore = 5,
    };

    private static ScoringAgent CreateAgent(FakeChatClient chat)
    {
        var options = Options.Create(new ScoringAgentOptions
        {
            ModelName = "test-model",
            AgentName = "scoring-agent",
            AgentVersion = "test",
        });
        return new ScoringAgent(chat, new FakeApiClient(), options, NullLogger<ScoringAgent>.Instance);
    }

    [Fact]
    public async Task ScoreAsync_returns_decision_from_model_output()
    {
        var chat = new FakeChatClient();
        chat.Enqueue("""
            {
              "eventSlug": "northwoods-tech-summit-2027",
              "recommendation": "SubmitNow",
              "rationale": "Strong fit on the AgentOps lane with low travel burden.",
              "fitScore": 9,
              "effortScore": 3,
              "confidenceScore": 8,
              "recommendedTalkSlug": "agentops-real-world"
            }
            """);

        var agent = CreateAgent(chat);
        var decision = await agent.ScoreAsync(SampleEvent, [SampleTalk]);

        Assert.Equal(Recommendation.SubmitNow, decision.Recommendation);
        Assert.Equal(9, decision.FitScore);
        Assert.Equal("agentops-real-world", decision.RecommendedTalkSlug);
        Assert.Equal("scoring-agent-test", decision.DecidedByAgent);
        Assert.Contains(SampleEvent.Slug, chat.CapturedMessages[0][1].Text);
    }

    [Fact]
    public async Task ScoreAsync_tolerates_json_fences_in_model_output()
    {
        var chat = new FakeChatClient();
        chat.Enqueue("""
            ```json
            {"eventSlug":"northwoods-tech-summit-2027","recommendation":"Monitor","rationale":"Topic fit is unclear from the listing","fitScore":5,"effortScore":5,"confidenceScore":4}
            ```
            """);

        var agent = CreateAgent(chat);
        var decision = await agent.ScoreAsync(SampleEvent, [SampleTalk]);

        Assert.Equal(Recommendation.Monitor, decision.Recommendation);
        Assert.Equal(5, decision.FitScore);
    }

    [Fact]
    public async Task ScoreAsync_throws_when_score_out_of_range()
    {
        var chat = new FakeChatClient();
        chat.Enqueue("""
            {"eventSlug":"northwoods-tech-summit-2027","recommendation":"SubmitNow","rationale":"x","fitScore":42,"effortScore":3,"confidenceScore":7}
            """);

        var agent = CreateAgent(chat);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => agent.ScoreAsync(SampleEvent, [SampleTalk]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ScoreAsync_throws_when_model_returns_no_content(string content)
    {
        var chat = new FakeChatClient();
        chat.Enqueue(content);

        var agent = CreateAgent(chat);
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.ScoreAsync(SampleEvent, [SampleTalk]));
    }

    [Theory]
    [InlineData("null")]                  // valid JSON literal, deserializes to null
    [InlineData("not json at all")]       // not JSON
    [InlineData("{\"fitScore\": 5")]      // truncated JSON
    public async Task ScoreAsync_throws_on_unparseable_model_output(string content)
    {
        var chat = new FakeChatClient();
        chat.Enqueue(content);

        var agent = CreateAgent(chat);
        // InvalidOperationException for "null", JsonException for malformed — both are failures.
        await Assert.ThrowsAnyAsync<Exception>(() => agent.ScoreAsync(SampleEvent, [SampleTalk]));
    }

    [Fact]
    public async Task ScoreAsync_overrides_slug_when_model_echoes_a_different_one()
    {
        var chat = new FakeChatClient();
        chat.Enqueue("""
            {"eventSlug":"some-hallucinated-slug","recommendation":"Monitor","rationale":"x","fitScore":5,"effortScore":5,"confidenceScore":5}
            """);

        var agent = CreateAgent(chat);
        var decision = await agent.ScoreAsync(SampleEvent, [SampleTalk]);

        // The real event's slug is recorded, not the one the model echoed back.
        Assert.Equal(SampleEvent.Slug, decision.EventSlug);
    }
}

internal sealed class FakeApiClient : ISpeakerPipelineApiClient
{
    public Task<IReadOnlyList<EventRecord>> GetEventsAsync(IReadOnlyList<EventCategory>? categories = null, TimeSpan? deadlineWindow = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<EventRecord>>([]);
    public Task<EventRecord?> GetEventAsync(string slug, CancellationToken ct = default) => Task.FromResult<EventRecord?>(null);
    public Task<EventRecord> UpsertEventAsync(EventRecord record, CancellationToken ct = default) => Task.FromResult(record);
    public Task<IReadOnlyList<SubmissionRecord>> GetSubmissionsForEventAsync(string eventSlug, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SubmissionRecord>>([]);
    public Task<SubmissionRecord> UpsertSubmissionAsync(SubmissionRecord record, CancellationToken ct = default) => Task.FromResult(record);
    public Task<IReadOnlyList<TalkRecord>> GetTalksAsync(Lane? lane = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TalkRecord>>([]);
    public Task<TalkRecord?> GetTalkAsync(string slug, CancellationToken ct = default) => Task.FromResult<TalkRecord?>(null);
    public Task<TalkRecord> UpsertTalkAsync(TalkRecord record, CancellationToken ct = default) => Task.FromResult(record);
    public Task<IReadOnlyList<EventRecord>> GetScoringCandidatesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<EventRecord>>([]);
    public Task PostScoringDecisionAsync(ScoringDecision decision, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<TopicRecord>> GetTopicsAsync(TopicStage? stage = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TopicRecord>>([]);
    public Task<TopicRecord?> GetTopicAsync(string topicId, CancellationToken ct = default) => Task.FromResult<TopicRecord?>(null);
    public Task<TopicRecord> UpsertTopicAsync(TopicRecord record, CancellationToken ct = default) => Task.FromResult(record);
    public Task<IReadOnlyList<BlackoutRecord>> GetBlackoutsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<BlackoutRecord>>([]);
    public Task<BlackoutRecord> UpsertBlackoutAsync(BlackoutRecord record, CancellationToken ct = default) => Task.FromResult(record);
    public Task<EventRecord> ApplyPipelineActionAsync(string slug, PipelineActionRequest request, CancellationToken ct = default) => throw new NotImplementedException();
}
