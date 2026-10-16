using System.Text.Json;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Agents.Scoring.Evals;

public sealed class LiveEvaluationFactAttribute : FactAttribute
{
    public LiveEvaluationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SCORING_EVALS_LIVE") != "1")
            Skip = "Explicit SCORING_EVALS_LIVE=1 and a Foundry deployment are required; no model calls in ordinary CI.";
    }
}

public sealed class CatalogLiveEvaluation
{
    [LiveEvaluationFact]
    public async Task Catalog_evidence_separates_fit_and_preparation_with_worldwide_eligibility()
    {
        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var configured = config.GetSection("ScoringAgent").Get<ScoringAgentOptions>() ?? new();
        var tenantId = Environment.GetEnvironmentVariable("SCORING_EVALS_TENANT_ID");
        Assert.True(Guid.TryParse(tenantId, out _), "SCORING_EVALS_TENANT_ID must explicitly select the personal tenant; CLI defaults are not used.");
        using var chat = new AzureOpenAIClient(new Uri(configured.Endpoint), new AzureCliCredential(new AzureCliCredentialOptions { TenantId = tenantId }))
            .GetChatClient(configured.ModelName).AsIChatClient();
        configured.MaxInputCharactersPerCandidate = 16000;
        configured.MaxOutputTokens = 4096;
        configured.AgentVersion = "catalog-live-eval";
        var scorer = new ScoringAgent(chat, new ScoringAgentEvalRunner.EvalApiClient(), Options.Create(configured), NullLogger<ScoringAgent>.Instance);
        var source = new CatalogSource("synthetic-evaluation", "talk.yaml", "fixture", "fixture");
        var talk = new CatalogTalk
        {
            TalkId = "agentops", CurrentVersionId = "current", Title = "Production Agent Evals and Tracing",
            Abstract = "A delivered 60-minute developer talk with working .NET demos: OpenTelemetry tracing, golden-set agent evaluation, regression testing and production incident debugging.",
            Topics = ["AI agents", ".NET", "OpenTelemetry", "evaluation"], Audiences = ["experienced developers"],
            Formats = ["conference-session"], Status = "completed", Source = source
        };
        var catalog = new CatalogSnapshot { GenerationId = "synthetic-catalog", Talks = [talk], Versions = [new() { TalkId = talk.TalkId, VersionId = "current", Title = talk.Title, Abstract = talk.Abstract, Source = source }] };
        EventRecord Event(string id, string name, string notes, string location = "Milwaukee, WI") => new()
        {
            Slug = id, Name = name, Notes = notes, Location = location, EventType = EventType.Conference,
            Category = EventCategory.Monitor, Priority = Priority.Medium, CfpStatus = CfpStatus.Open,
            CfpDeadline = DateTimeOffset.UtcNow.AddDays(21), EventDateStart = DateTimeOffset.UtcNow.AddMonths(5),
            CfpUrl = "https://example.org/cfp", Format = EventFormat.InPerson
        };
        var cases = new[]
        {
            Event("exact", "AgentOps Developer Conference", "Confirmed track: production .NET agent evals and OpenTelemetry tracing for experienced developers. Existing 60-minute deck and demos can be repeated unchanged."),
            Event("audience", "Executive AI Strategy Forum", "Audience is nontechnical executives; wants business strategy and operating budgets, no code or technical tracing."),
            Event("unrelated", "Freshwater Fishing Symposium", "Only freshwater fish habitat and fishing technique sessions. No software, AI, developer or enterprise technology tracks."),
            Event("heavy", "AgentOps Developer Workshop", "Confirmed track: production .NET agent evaluation and tracing. Requires an entirely new full-day workshop with custom labs and a fresh demo application."),
            Event("international", "International AgentOps Developer Conference", "Confirmed track: production .NET agent evals and OpenTelemetry tracing for experienced developers. Existing 60-minute deck and demos can be repeated unchanged. Travel reimbursement unknown.", "London, UK")
        };
        // Exactly five requests, max 4096 total completion tokens each, including reasoning.
        var decisions = new List<ScoringDecision>();
        foreach (var item in cases) decisions.Add(await scorer.ScoreAsync(item, [], catalog: catalog));
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "catalog-live-evals-report.json"),
            JsonSerializer.Serialize(new { Rubric = ScoringRubric.Version, Model = configured.ModelName, MaximumCalls = 5, MaximumOutputTokens = 20480, Decisions = decisions }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(decisions[0].FitScore >= 7, "Direct topic and audience match should be strong.");
        Assert.True(decisions[1].FitScore <= 6, "Wrong audience must reduce fit despite shared AI keywords.");
        Assert.True(decisions[2].FitScore <= 3, "Unrelated topic must not receive compressed high fit.");
        Assert.True(decisions[3].EffortScore >= 7 && decisions[3].EffortScore > decisions[0].EffortScore, "More new work must raise effort.");
        Assert.True(decisions[4].FitScore >= 7, "International geography must not erase strong topic fit.");
        Assert.All(decisions.Where(d => d.Recommendation is Recommendation.SubmitNow or Recommendation.Outreach), d => Assert.NotEmpty(d.MatchedTalks));
    }
}
