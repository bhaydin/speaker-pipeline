using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Agents.Scoring;

/// <summary>
/// The scoring agent. Wraps an <see cref="IChatClient"/> from
/// Microsoft.Extensions.AI so it stays provider-agnostic, and emits an
/// OpenTelemetry activity per scoring decision.
///
/// In Microsoft Agent Framework 1.0, IChatClient is also the substrate for
/// AIAgent; promoting this to a full AIAgent (with tools, threads) is a
/// straightforward future step and does not change the data contract.
/// </summary>
public sealed class ScoringAgent
{
    public const string ActivitySourceName = "SpeakerPipeline.Agents.Scoring";
    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    private readonly IChatClient _chat;
    private readonly ISpeakerPipelineApiClient _api;
    private readonly ILogger<ScoringAgent> _logger;
    private readonly ScoringAgentOptions _options;

    public ScoringAgent(
        IChatClient chat,
        ISpeakerPipelineApiClient api,
        IOptions<ScoringAgentOptions> options,
        ILogger<ScoringAgent> logger)
    {
        _chat = chat;
        _api = api;
        _logger = logger;
        _options = options.Value;
    }

    /// <summary>
    /// Pulls candidate events from the API, scores each, and posts decisions
    /// back. Returns the decisions produced (in order) for observability and
    /// for tests.
    /// </summary>
    public async Task<IReadOnlyList<ScoredVerdict>> RunAsync(CancellationToken ct = default)
    {
        using var runActivity = ActivitySource.StartActivity("scoring-agent.run");
        runActivity?.SetTag("agent.name", _options.AgentName);
        runActivity?.SetTag("agent.version", _options.AgentVersion);
        runActivity?.SetTag("model.name", _options.ModelName);

        var candidates = await _api.GetScoringCandidatesAsync(ct);
        if (candidates.Count == 0)
        {
            _logger.LogInformation("Scoring agent run: no candidates");
            return [];
        }

        var capped = candidates.Take(_options.MaxCandidatesPerRun).ToArray();
        runActivity?.SetTag("candidates.count", capped.Length);

        var talks = await _api.GetTalksAsync(ct: ct);

        // One context assembly per run — the scorer windows it per candidate.
        var context = await _api.GetPipelineContextAsync(ct);
        runActivity?.SetTag("context.committed", context.Committed.Count);
        runActivity?.SetTag("context.blackouts", context.Blackouts.Count);

        var verdicts = new List<ScoredVerdict>(capped.Length);

        foreach (var ev in capped)
        {
            try
            {
                // Capture the prior state before posting the decision overwrites it.
                var change = ClassifyChange(ev, await ScoreThenPost(ev, talks, context, ct));
                verdicts.Add(change);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Scoring failed for event {Slug}", ev.Slug);
            }
        }

        runActivity?.SetTag("decisions.count", verdicts.Count);
        runActivity?.SetTag("verdicts.changed", verdicts.Count(v => v.Change == VerdictChange.Changed));
        return verdicts;
    }

    private async Task<ScoringDecision> ScoreThenPost(
        EventRecord ev, IReadOnlyList<TalkRecord> talks, PipelineContext context, CancellationToken ct)
    {
        var decision = await ScoreAsync(ev, talks, context, ct);
        await _api.PostScoringDecisionAsync(decision, ct);
        return decision;
    }

    /// <summary>
    /// Classifies a fresh decision against the event's prior state: New when the
    /// event was never scored, Changed when the recommendation moves it to a
    /// different category (a verdict flip — the interesting signal), else Unchanged.
    /// </summary>
    private static ScoredVerdict ClassifyChange(EventRecord priorEvent, ScoringDecision decision)
    {
        var wasDecided = !string.IsNullOrWhiteSpace(priorEvent.DecidedByAgent);
        var change = !wasDecided
            ? VerdictChange.New
            : ScoringMap.ToCategory(decision.Recommendation) != priorEvent.Category
                ? VerdictChange.Changed
                : VerdictChange.Unchanged;

        return new ScoredVerdict(decision, priorEvent.Category, change);
    }

    /// <summary>
    /// Scores a single event. Public so the eval suite can exercise it
    /// directly with goldens. <paramref name="context"/> supplies the calendar
    /// view the rubric's congestion/travel factors need; pass null to score
    /// without it.
    /// </summary>
    public async Task<ScoringDecision> ScoreAsync(
        EventRecord eventRecord, IReadOnlyList<TalkRecord> talks, PipelineContext? context = null, CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("scoring-agent.score");
        activity?.SetTag("agent.name", _options.AgentName);
        activity?.SetTag("agent.version", _options.AgentVersion);
        activity?.SetTag("model.name", _options.ModelName);
        activity?.SetTag("event.slug", eventRecord.Slug);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, ScoringRubric.SystemPrompt),
            new(ChatRole.User, ScoringRubric.BuildUserPrompt(eventRecord, talks, context ?? PipelineContext.Empty)),
        };

        _logger.LogDebug("Scoring agent prompt for {Slug}: {Prompt}", eventRecord.Slug, messages[1].Text);

        var options = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.Json,
            ModelId = _options.ModelName,
        };

        var response = await _chat.GetResponseAsync(messages, options, ct);
        var raw = response.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant)?.Text ?? response.Text;
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException($"Scoring model returned no content for {eventRecord.Slug}.");
        }

        var parsed = ParseDecision(raw, eventRecord.Slug);
        ScoringDecision.ValidateScore(parsed.FitScore, nameof(parsed.FitScore));
        ScoringDecision.ValidateScore(parsed.EffortScore, nameof(parsed.EffortScore));
        ScoringDecision.ValidateScore(parsed.ConfidenceScore, nameof(parsed.ConfidenceScore));

        var decision = parsed with
        {
            DecidedByAgent = $"{_options.AgentName}-{_options.AgentVersion}",
        };

        activity?.SetTag("decision.recommendation", decision.Recommendation.ToString());
        activity?.SetTag("decision.fit_score", decision.FitScore);
        activity?.SetTag("decision.effort_score", decision.EffortScore);
        activity?.SetTag("decision.confidence_score", decision.ConfidenceScore);

        _logger.LogInformation(
            "Scored {Slug} → {Recommendation} (fit={Fit}, effort={Effort}, confidence={Confidence})",
            eventRecord.Slug, decision.Recommendation, decision.FitScore, decision.EffortScore, decision.ConfidenceScore);

        return decision;
    }

    internal static ScoringDecision ParseDecision(string rawJson, string expectedSlug)
    {
        // Some models prefix with ```json fences; tolerate them.
        var trimmed = rawJson.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline >= 0)
            {
                trimmed = trimmed[(firstNewline + 1)..];
            }
            if (trimmed.EndsWith("```", StringComparison.Ordinal))
            {
                trimmed = trimmed[..^3];
            }
        }

        var dto = JsonSerializer.Deserialize<ScoringDecisionDto>(trimmed, JsonOpts)
            ?? throw new InvalidOperationException("Scoring model returned non-JSON output.");

        if (!string.Equals(dto.EventSlug, expectedSlug, StringComparison.OrdinalIgnoreCase))
        {
            // Tolerate model echoing a different slug but record the real one.
            dto = dto with { EventSlug = expectedSlug };
        }

        return new ScoringDecision
        {
            EventSlug = dto.EventSlug,
            Recommendation = dto.Recommendation,
            Rationale = dto.Rationale ?? string.Empty,
            FitScore = dto.FitScore,
            EffortScore = dto.EffortScore,
            ConfidenceScore = dto.ConfidenceScore,
            RecommendedTalkSlug = dto.RecommendedTalkSlug,
        };
    }

    private sealed record ScoringDecisionDto
    {
        public string EventSlug { get; init; } = string.Empty;
        public Recommendation Recommendation { get; init; }
        public string? Rationale { get; init; }
        public int FitScore { get; init; }
        public int EffortScore { get; init; }
        public int ConfidenceScore { get; init; }
        public string? RecommendedTalkSlug { get; init; }
    }
}

/// <summary>
/// A scoring decision plus how it relates to what the tracker already held for
/// the event — the input the digest uses to headline verdict flips.
/// </summary>
public sealed record ScoredVerdict(ScoringDecision Decision, EventCategory PriorCategory, VerdictChange Change);
