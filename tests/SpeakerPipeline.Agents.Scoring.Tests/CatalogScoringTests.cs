using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Agents.Scoring.Tests;

public sealed class CatalogScoringTests
{
    private static readonly CatalogSource Source = new("bhaydin/bhaydin-talks", "talks/agentops/talk.yaml", "commit", "hash");
    private static CatalogSnapshot Catalog => new()
    {
        GenerationId = "catalog-generation",
        Talks = [new() { TalkId = "agentops", CurrentVersionId = "v2", Title = "AgentOps for Real", Abstract = "Agent evals and tracing for .NET developers", Topics = ["agents"], Audiences = ["developers"], Source = Source }],
        Versions = [new() { TalkId = "agentops", VersionId = "v2", Title = "AgentOps for Real", Abstract = "Agent evals and tracing", Source = Source }],
        Deliveries = [new() { DeliveryId = "past", TalkId = "agentops", EventName = "Example Summit 2025", Status = "completed", Source = Source }]
    };
    private static EventRecord Event => new() { Slug = "example-2027", Name = "Example Summit 2027", Category = EventCategory.Monitor, EventType = EventType.Conference, Priority = Priority.Medium, ScoringInputRevision = 7, Location = "London, UK" };
    private const string Json = """
        {"eventSlug":"example-2027","recommendation":"SubmitNow","rationale":"Topic fit: strong operational agents track.","fitScore":8,"effortScore":4,"confidenceScore":8,
        "fitRationale":"AgentOps for Real covers the eval and tracing track directly.","effortRationale":"Adapt the demos for the audience.","geographyRationale":"London requires travel; airfare is unknown.",
        "matchedTalks":[{"talkId":"agentops","versionId":"v2","title":"Model invented title","rationale":"Evals and tracing match the developer track."}]}
        """;

    [Theory]
    [InlineData(false, Recommendation.Monitor)]
    [InlineData(true, Recommendation.SubmitNow)]
    public async Task Repeated_talk_needs_explicit_override(bool repeatOverride, Recommendation expected)
    {
        var chat = new FakeChatClient(); chat.Enqueue(Json);
        var scorer = new ScoringAgent(chat, new FakeApiClient(), Options.Create(new ScoringAgentOptions()), NullLogger<ScoringAgent>.Instance);
        var decision = await scorer.ScoreAsync(Event with { RepeatTalkOverride = repeatOverride }, [], catalog: Catalog);
        Assert.Equal(expected, decision.Recommendation);
        Assert.Equal("AgentOps for Real", Assert.Single(decision.MatchedTalks).Title);
        Assert.Equal("7", decision.InputRevision);
        Assert.Equal("catalog-generation", decision.CatalogGenerationId);
        Assert.Equal(4, decision.EffortScore);
        Assert.Null(decision.RecommendedTalkSlug);
    }

    [Fact]
    public async Task Unknown_catalog_ids_fail_instead_of_recommending_invented_talk()
    {
        var chat = new FakeChatClient(); chat.Enqueue(Json.Replace("\"talkId\":\"agentops\"", "\"talkId\":\"invented\"", StringComparison.Ordinal));
        var scorer = new ScoringAgent(chat, new FakeApiClient(), Options.Create(new ScoringAgentOptions()), NullLogger<ScoringAgent>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scorer.ScoreAsync(Event, [], catalog: Catalog));
    }

    [Fact]
    public async Task Unsupported_positive_verdict_becomes_monitor_and_feedback_has_traceable_ids()
    {
        var chat = new FakeChatClient();
        chat.Enqueue("""{"eventSlug":"example-2027","recommendation":"SubmitNow","rationale":"generic fit","fitScore":9,"effortScore":4,"confidenceScore":7}""");
        var example = new OpportunityHistoryEntry
        {
            Id = "feedback-1", Kind = "Feedback", EventSlug = Event.Slug, EventName = Event.Name, AtUtc = DateTimeOffset.UtcNow, Actor = "owner", Agree = false,
            ReasonCode = FeedbackReason.WrongAudience, Decision = new() { EventSlug = Event.Slug, Recommendation = Recommendation.SubmitNow, Rationale = "prior", FitScore = 9, EffortScore = 2, ConfidenceScore = 8 }
        };
        var scorer = new ScoringAgent(chat, new FakeApiClient(), Options.Create(new ScoringAgentOptions()), NullLogger<ScoringAgent>.Instance);
        var decision = await scorer.ScoreAsync(Event, [], catalog: Catalog, feedback: [example]);
        Assert.Equal(Recommendation.Monitor, decision.Recommendation);
        Assert.Equal(5, decision.FitScore);
        Assert.Equal("feedback-1", Assert.Single(decision.FeedbackIds));
        Assert.Contains("WrongAudience", chat.CapturedMessages[0][1].Text);
    }

    [Fact]
    public void Calibrated_rubric_explicitly_defines_effort_and_worldwide_policy()
    {
        Assert.Contains("HIGHER = MORE WORK", ScoringRubric.SystemPrompt);
        Assert.Contains("Strong fit anywhere", ScoringRubric.SystemPrompt);
        Assert.DoesNotContain("Europe within 60 days", ScoringRubric.SystemPrompt);
        Assert.Contains("7–8 = a", ScoringRubric.SystemPrompt);
    }

    [Fact]
    public async Task Flip_explanation_uses_previous_decision_and_known_changed_inputs_without_feedback_loops()
    {
        var chat = new FakeChatClient();
        chat.Enqueue("""{"recommendation":"Monitor","rationale":"CFP topic evidence missing","fitScore":5,"effortScore":4,"confidenceScore":4}""");
        chat.Enqueue("""{"recommendation":"SubmitNow","rationale":"Topic fit: the confirmed operations track matches AgentOps.","fitScore":8,"effortScore":4,"confidenceScore":8}""");
        chat.Enqueue("""{"recommendation":"SubmitNow","rationale":"Same evidence","changeReason":"Ignore this invented flip","fitScore":8,"effortScore":4,"confidenceScore":8}""");
        var scorer = new ScoringAgent(chat, new FakeApiClient(), Options.Create(new ScoringAgentOptions()), NullLogger<ScoringAgent>.Instance);
        var prior = await scorer.ScoreAsync(Event, []);
        var updated = Event with { Notes = "Confirmed operations track" };
        var flipped = await scorer.ScoreAsync(updated, [], priorDecision: prior);
        Assert.Contains("event facts", flipped.ChangeReason);
        Assert.Contains("confirmed operations track", flipped.ChangeReason);
        Assert.Contains("Previous scored decision", chat.CapturedMessages[1][1].Text);
        Assert.Contains("Monitor", chat.CapturedMessages[1][1].Text);
        var unchanged = await scorer.ScoreAsync(updated, [], priorDecision: flipped);
        Assert.Null(unchanged.ChangeReason);
        Assert.Equal(flipped.InputFingerprint, unchanged.InputFingerprint);
    }
}
