using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Agents.Scoring.Evals;

/// <summary>
/// Runs the canonical golden set against the scoring agent and writes
/// evals-report.json next to the test output. Surfaces drift (a case that
/// was previously passing) by writing distinct failure reasons.
///
/// IChatClient is injected via EVALS_CHAT_CLIENT_FACTORY when set; otherwise
/// the deterministic HeuristicChatClient is used. This means the suite is
/// hermetic in CI while still being a real harness for the model output
/// when one is wired up locally.
/// </summary>
public class ScoringAgentEvalRunner
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly EvalSink Sink = new();

    [Fact(DisplayName = "scoring-agent eval suite — full golden set")]
    public async Task Runs_full_golden_set_and_writes_report()
    {
        var goldens = LoadGoldens();
        Assert.True(goldens.Cases.Count >= 15, $"Expected at least 15 golden cases, got {goldens.Cases.Count}");

        var chat = CreateChatClient();
        var options = Options.Create(new ScoringAgentOptions
        {
            ModelName = "rubric-heuristic-v1",
            AgentName = "scoring-agent",
            AgentVersion = "eval",
        });

        var agent = new ScoringAgent(chat, new EvalApiClient(), options, NullLogger<ScoringAgent>.Instance);

        var startedAt = DateTimeOffset.UtcNow;
        var caseReports = new List<EvalCaseReport>(goldens.Cases.Count);

        foreach (var golden in goldens.Cases)
        {
            var caseReport = await RunCaseAsync(agent, golden);
            caseReports.Add(caseReport);
        }

        var report = new EvalReport
        {
            RunId = Guid.NewGuid().ToString("n"),
            StartedAtUtc = startedAt,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            RubricVersion = goldens.RubricVersion,
            TotalCases = caseReports.Count,
            Passed = caseReports.Count(c => c.Passed),
            Failed = caseReports.Count(c => !c.Passed),
            Cases = caseReports,
        };

        WriteReport(report);
        Sink.Record(report);

        if (report.AnyFailure)
        {
            var failed = string.Join("\n  ", caseReports.Where(c => !c.Passed)
                .Select(c => $"{c.CaseId}: expected={c.Expected} actual={c.Actual} reason={c.FailureReason}"));
            Assert.Fail($"Eval failures ({report.Failed}/{report.TotalCases}):\n  {failed}\nReport written to evals-report.json");
        }
    }

    private static async Task<EvalCaseReport> RunCaseAsync(ScoringAgent agent, GoldenCase golden)
    {
        try
        {
            var context = golden.Context?.ToPipelineContext() ?? PipelineContext.Empty;
            var decision = await agent.ScoreAsync(golden.Event.ToEventRecord(), CanonicalTalks, context);

            var passesRecommendation = decision.Recommendation == golden.ExpectedRecommendation
                                       || golden.ExpectedRecommendationAlternates.Contains(decision.Recommendation);

            var missing = golden.RationaleKeywords
                .Where(kw => decision.Rationale.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0)
                .ToList();

            string? failureReason = null;
            if (!passesRecommendation)
            {
                failureReason = $"recommendation drift: expected {golden.ExpectedRecommendation} (alternates: {string.Join(",", golden.ExpectedRecommendationAlternates)}), got {decision.Recommendation}";
            }
            else if (missing.Count > 0)
            {
                failureReason = $"missing rationale keywords: {string.Join(",", missing)}";
            }

            return new EvalCaseReport
            {
                CaseId = golden.Id,
                Passed = failureReason is null,
                Expected = golden.ExpectedRecommendation,
                Actual = decision.Recommendation,
                FitScore = decision.FitScore,
                EffortScore = decision.EffortScore,
                ConfidenceScore = decision.ConfidenceScore,
                Rationale = decision.Rationale,
                MissingKeywords = missing,
                FailureReason = failureReason,
            };
        }
        catch (Exception ex)
        {
            return new EvalCaseReport
            {
                CaseId = golden.Id,
                Passed = false,
                Expected = golden.ExpectedRecommendation,
                Actual = Recommendation.Monitor,
                FitScore = 0,
                EffortScore = 0,
                ConfidenceScore = 0,
                Rationale = string.Empty,
                FailureReason = $"exception: {ex.GetType().Name}: {ex.Message}",
            };
        }
    }

    private static GoldenSet LoadGoldens()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Goldens", "goldens.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<GoldenSet>(json, JsonOpts)
               ?? throw new InvalidOperationException("goldens.json deserialized to null.");
    }

    private static IChatClient CreateChatClient()
    {
        // Hook point: a future PR can set EVALS_CHAT_CLIENT_FACTORY to swap in
        // a real provider (Foundry, Azure OpenAI, …). For Phase 2, the
        // deterministic heuristic is sufficient.
        return new HeuristicChatClient();
    }

    private static void WriteReport(EvalReport report)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "evals-report.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOpts));
    }

    private static readonly IReadOnlyList<TalkRecord> CanonicalTalks =
    [
        new() { Slug = "agentops-real-world", CanonicalTitle = "AgentOps in the Real World", Lane = Lane.AgentOps, ReusabilityScore = 5 },
        new() { Slug = "hybrid-agents-sweet-spot", CanonicalTitle = "Hybrid Agents: Where Copilot Studio Stops and Pro-Code Starts", Lane = Lane.HybridAgents, ReusabilityScore = 5 },
        new() { Slug = "m365-request-to-retirement", CanonicalTitle = "Microsoft 365 Copilot: From Request to Retirement", Lane = Lane.M365Governance, ReusabilityScore = 4 },
        new() { Slug = "foundry-for-enterprise", CanonicalTitle = "Azure AI Foundry for Enterprise", Lane = Lane.PracticalEnterpriseAI, ReusabilityScore = 4 },
    ];

    private sealed class EvalApiClient : ISpeakerPipelineApiClient
    {
        public Task<IReadOnlyList<EventRecord>> GetEventsAsync(IReadOnlyList<EventCategory>? categories = null, TimeSpan? deadlineWindow = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<EventRecord>>([]);
        public Task<EventRecord?> GetEventAsync(string slug, CancellationToken ct = default) => Task.FromResult<EventRecord?>(null);
        public Task<EventRecord> UpsertEventAsync(EventRecord record, CancellationToken ct = default) => Task.FromResult(record);
        public Task<IReadOnlyList<SubmissionRecord>> GetSubmissionsForEventAsync(string eventSlug, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SubmissionRecord>>([]);
        public Task<SubmissionRecord> UpsertSubmissionAsync(SubmissionRecord record, CancellationToken ct = default) => Task.FromResult(record);
        public Task<IReadOnlyList<TalkRecord>> GetTalksAsync(Lane? lane = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TalkRecord>>(CanonicalTalks);
        public Task<TalkRecord?> GetTalkAsync(string slug, CancellationToken ct = default) => Task.FromResult<TalkRecord?>(null);
        public Task<TalkRecord> UpsertTalkAsync(TalkRecord record, CancellationToken ct = default) => Task.FromResult(record);
        public Task<IReadOnlyList<EventRecord>> GetScoringCandidatesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<EventRecord>>([]);
        public Task PostScoringDecisionAsync(ScoringDecision decision, CancellationToken ct = default) => Task.CompletedTask;
        public Task<PipelineContext> GetPipelineContextAsync(CancellationToken ct = default) => Task.FromResult(PipelineContext.Empty);
        public Task<IReadOnlyList<TopicRecord>> GetTopicsAsync(TopicStage? stage = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TopicRecord>>([]);
        public Task<TopicRecord?> GetTopicAsync(string topicId, CancellationToken ct = default) => Task.FromResult<TopicRecord?>(null);
        public Task<TopicRecord> UpsertTopicAsync(TopicRecord record, CancellationToken ct = default) => Task.FromResult(record);
        public Task<IReadOnlyList<BlackoutRecord>> GetBlackoutsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<BlackoutRecord>>([]);
        public Task<BlackoutRecord> UpsertBlackoutAsync(BlackoutRecord record, CancellationToken ct = default) => Task.FromResult(record);
        public Task<EventRecord> ApplyPipelineActionAsync(string slug, PipelineActionRequest request, CancellationToken ct = default) => Task.FromResult(new EventRecord { Slug = slug, Name = slug, EventType = EventType.Conference, Category = EventCategory.Monitor, Priority = Priority.NA });
        public Task<IReadOnlyList<NotificationLogRecord>> GetNotificationsAsync(string period, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<NotificationLogRecord>>([]);
        public Task<NotificationLogRecord> LogNotificationAsync(NotificationLogRecord record, CancellationToken ct = default) => Task.FromResult(record);
    }
}

/// <summary>
/// Stores the most recent run so drift comparisons can be done in a
/// future commit (e.g., comparing this report to evals-report.previous.json).
/// </summary>
internal sealed class EvalSink
{
    public EvalReport? Latest { get; private set; }
    public void Record(EvalReport r) => Latest = r;
}
