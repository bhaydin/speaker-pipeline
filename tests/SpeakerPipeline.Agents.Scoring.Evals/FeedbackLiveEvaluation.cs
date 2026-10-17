using System.Text.Json;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Agents.Scoring.Evals;

public sealed class FeedbackLiveEvaluation
{
    [LiveEvaluationFact]
    public async Task Event_specific_feedback_changes_timing_without_globalizing_unrelated_preferences()
    {
        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var options = config.GetSection("ScoringAgent").Get<ScoringAgentOptions>() ?? new();
        var tenantId = Environment.GetEnvironmentVariable("SCORING_EVALS_TENANT_ID");
        Assert.True(Guid.TryParse(tenantId, out _), "An explicit deployment tenant is required; CLI defaults are not used.");
        using var chat = new AzureOpenAIClient(new Uri(options.Endpoint), new AzureCliCredential(new AzureCliCredentialOptions { TenantId = tenantId }))
            .GetChatClient(options.ModelName).AsIChatClient();
        options.MaxInputCharactersPerCandidate = 16000;
        options.MaxOutputTokens = 4096;
        options.AgentVersion = "feedback-live-eval";
        var scorer = new ScoringAgent(chat, new ScoringAgentEvalRunner.EvalApiClient(), Options.Create(options), NullLogger<ScoringAgent>.Instance);
        var now = DateTimeOffset.UtcNow;
        var source = new CatalogSource("synthetic-evaluation", "talk.yaml", "fixture", "fixture");
        var talk = new CatalogTalk
        {
            TalkId = "canonical-agentops", CurrentVersionId = "current-v2", Title = "Production Agent Evals and Tracing",
            Abstract = "An existing delivered 60-minute developer talk with working .NET demos: OpenTelemetry tracing, golden-set agent evaluation, regression testing and production incident debugging.",
            Topics = ["AI agents", ".NET", "OpenTelemetry", "evaluation"], Audiences = ["experienced developers"],
            Formats = ["conference-session"], Status = "completed", Source = source,
        };
        var catalog = new CatalogSnapshot
        {
            GenerationId = "synthetic-feedback-catalog", Talks = [talk],
            Versions = [new() { TalkId = talk.TalkId, VersionId = talk.CurrentVersionId, Title = talk.Title, Abstract = talk.Abstract, Source = source }],
        };
        var candidate = new EventRecord
        {
            Slug = "fictional-local-agentops", Name = "Fictional Local AgentOps Developer Conference",
            Notes = "Confirmed CFP track: production .NET agent evals and OpenTelemetry tracing for experienced developers. A 60-minute conference session; the existing delivered deck and working demos can be reused unchanged.",
            Location = "Milwaukee, WI", Format = EventFormat.InPerson, EventType = EventType.Conference,
            Category = EventCategory.Monitor, Priority = Priority.Medium, CfpStatus = CfpStatus.Open,
            CfpDeadline = now.AddDays(30), EventDateStart = now.AddDays(150), EventDateEnd = now.AddDays(150),
            CfpUrl = "https://example.invalid/cfp",
        };
        ScoringDecision Prior(string slug, string talkId = "canonical-agentops") => new()
        {
            EventSlug = slug, Recommendation = Recommendation.SubmitNow, Rationale = "Topic fit: direct developer agent operations track.",
            FitScore = 9, EffortScore = 2, ConfidenceScore = 8,
            MatchedTalks = [new(talkId, talk.CurrentVersionId, talkId == talk.TalkId ? talk.Title : "Freshwater Gardening", "Prior abstract and audience evidence.")],
        };
        var relevant = new OpportunityHistoryEntry
        {
            Id = "owner-event-unavailable", Kind = "Feedback", Actor = "synthetic-owner", AtUtc = now,
            EventSlug = candidate.Slug, EventName = candidate.Name, EventSnapshot = candidate,
            Agree = false, ReasonCode = FeedbackReason.BadTiming, DecisionId = "prior-event-decision", Decision = Prior(candidate.Slug),
            Reason = $"I cannot attend this event on {candidate.EventDateStart:yyyy-MM-dd}: I have a fixed family commitment that entire day. This is specific to these event dates; the talk and audience fit remain strong.",
        };
        var unrelated = new OpportunityHistoryEntry
        {
            Id = "owner-unrelated-gardening", Kind = "Feedback", Actor = "synthetic-owner", AtUtc = now,
            EventSlug = "freshwater-gardening", EventName = "Freshwater Gardening Symposium",
            Agree = false, ReasonCode = FeedbackReason.NotMyTopic, DecisionId = "prior-gardening-decision", Decision = Prior("freshwater-gardening", "gardening"),
            Reason = "Aquatic plants and garden ponds are outside my speaking topics.",
        };
        var distantTrip = new OpportunityHistoryEntry
        {
            Id = "owner-one-distant-trip", Kind = "Feedback", Actor = "synthetic-owner", AtUtc = now,
            EventSlug = "remote-island-invitation", EventName = "Remote Island Developer Gathering",
            Agree = false, ReasonCode = FeedbackReason.TooFar, DecisionId = "prior-trip-decision", Decision = Prior("remote-island-invitation"),
            Reason = "This remote island trip needs 30 hours of connections and several nights away for a single short session. That particular trip is too far; this is not a general travel preference.",
        };
        var cases = new (string Id, IReadOnlyList<OpportunityHistoryEntry> Feedback)[]
        {
            ("baseline", []), ("same-event-unavailable", [relevant]),
            ("unrelated-topic", [unrelated]), ("different-event-too-far", [distantTrip]),
        };
        var results = new List<(string Id, ScoringDecision Decision)>();
        // Exactly four synthetic calls. No API reads/writes or tracker mutations; max 16,384
        // completion tokens including reasoning, with a bounded prompt for every request.
        foreach (var item in cases)
            results.Add((item.Id, await scorer.ScoreAsync(candidate, [], catalog: catalog, feedback: item.Feedback)));
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "feedback-live-evals-report.json"), JsonSerializer.Serialize(new
        {
            Rubric = ScoringRubric.Version, Model = options.ModelName, options.ModelRevision,
            MaximumCalls = 4, MaximumInputCharactersPerCall = 16000, MaximumCompletionTokens = 16384,
            ActualInputTokens = results.Sum(r => r.Decision.InputTokens ?? 0),
            ActualCompletionTokens = results.Sum(r => r.Decision.OutputTokens ?? 0),
            ActualReasoningTokens = results.Sum(r => r.Decision.ReasoningTokens ?? 0),
            UsageComplete = results.All(r => r.Decision.InputTokens is not null && r.Decision.OutputTokens is not null),
            Limitations = "One bounded synthetic run; no statistical stability or broad preference-learning claim. Reasoning tokens are a subset of completion usage, not added twice.",
            Cases = results.Select(r => new { r.Id, r.Decision }),
        }, new JsonSerializerOptions { WriteIndented = true }));

        ValidateDecisions(results.Select(r => r.Decision).ToArray());
    }

    // Recheck preserved synthetic responses when an assertion's wording is corrected,
    // without paying for a second model run or misreporting replay as new inference.
    public static void ValidateSavedReport(string path)
    {
        using var report = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(ScoringRubric.Version, report.RootElement.GetProperty("Rubric").GetString());
        var cases = report.RootElement.GetProperty("Cases").EnumerateArray().ToArray();
        Assert.Equal(new[] { "baseline", "same-event-unavailable", "unrelated-topic", "different-event-too-far" },
            cases.Select(c => c.GetProperty("Id").GetString()).ToArray());
        ValidateDecisions(cases.Select(c => c.GetProperty("Decision").Deserialize<ScoringDecision>()!).ToArray());
    }

    private static void ValidateDecisions(IReadOnlyList<ScoringDecision> results)
    {
        Assert.Equal(4, results.Count);
        var baseline = results[0];
        var timing = results[1];
        Assert.Equal(Recommendation.SubmitNow, baseline.Recommendation);
        Assert.True(timing.Recommendation is Recommendation.Monitor or Recommendation.Pass or Recommendation.Skip,
            "Explicit unavailability on this event's dates must change its otherwise actionable recommendation.");
        Assert.Contains("owner-event-unavailable", timing.FeedbackIds);
        var explanation = string.Join(" ", timing.Rationale, timing.FitRationale, timing.ChangeReason, timing.GeographyRationale);
        Assert.True(explanation.Contains("family", StringComparison.OrdinalIgnoreCase) || explanation.Contains("unavailab", StringComparison.OrdinalIgnoreCase)
            || explanation.Contains("cannot attend", StringComparison.OrdinalIgnoreCase), "The changed recommendation must cite the event-specific timing evidence.");
        Assert.Equal(baseline.Recommendation, results[2].Recommendation);
        Assert.Empty(results[2].FeedbackIds);
        Assert.Equal(baseline.Recommendation, results[3].Recommendation);
        Assert.Contains("owner-one-distant-trip", results[3].FeedbackIds);
        Assert.All(results, result =>
        {
            Assert.Equal("synthetic-feedback-catalog", result.CatalogGenerationId);
            Assert.All(result.MatchedTalks, match =>
            {
                Assert.Equal("canonical-agentops", match.TalkId);
                Assert.Equal("current-v2", match.VersionId);
                Assert.Equal("Production Agent Evals and Tracing", match.Title);
                Assert.False(string.IsNullOrWhiteSpace(match.Rationale));
            });
            if (result.Recommendation is Recommendation.SubmitNow or Recommendation.Outreach)
                Assert.NotEmpty(result.MatchedTalks);
        });
    }
}
