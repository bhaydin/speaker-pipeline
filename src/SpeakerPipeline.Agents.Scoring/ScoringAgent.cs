using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
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
    private readonly ICatalogApiClient? _catalogApi;
    private readonly IOpportunityApiClient? _opportunities;

    public ScoringAgent(
        IChatClient chat,
        ISpeakerPipelineApiClient api,
        IOptions<ScoringAgentOptions> options,
        ILogger<ScoringAgent> logger,
        ICatalogApiClient? catalogApi = null,
        IOpportunityApiClient? opportunities = null)
    {
        _chat = chat;
        _api = api;
        _logger = logger;
        _options = options.Value;
        _catalogApi = catalogApi;
        _opportunities = opportunities;
    }

    /// <summary>
    /// Pulls candidate events from the API, scores each, and posts decisions
    /// back. Returns the decisions produced (in order) for observability and
    /// for tests.
    /// </summary>
    public async Task<IReadOnlyList<ScoredVerdict>> RunAsync(CancellationToken ct = default)
        => (await RunWithReportAsync(ct)).Decisions;

    public async Task<ScoringRunReport> RunWithReportAsync(CancellationToken ct = default)
    {
        using var runActivity = ActivitySource.StartActivity("scoring-agent.run");
        runActivity?.SetTag("agent.name", _options.AgentName);
        runActivity?.SetTag("agent.version", _options.AgentVersion);
        runActivity?.SetTag("model.name", _options.ModelName);

        var candidates = await _api.GetScoringCandidatesAsync(ct);
        if (candidates.Count == 0)
        {
            _logger.LogInformation("Scoring agent run: no candidates");
            return new ScoringRunReport(0, [], []);
        }

        var talks = await _api.GetTalksAsync(ct: ct);
        CatalogSnapshot? catalog = null;
        if (_options.CatalogEnabled)
        {
            if (_catalogApi is null) throw new InvalidOperationException("Catalog-aware scoring requires ICatalogApiClient.");
            catalog = await _catalogApi.GetCatalogAsync(ct);
            if (catalog.Talks.Count == 0) return new ScoringRunReport(candidates.Count, [], [new("", "CatalogUnavailable")]);
        }
        if (_options.UseOpportunityState && _opportunities is null) throw new InvalidOperationException("Opportunity scoring requires IOpportunityApiClient.");
        var feedback = !_options.UseOpportunityState ? [] : await _opportunities!.GetOpportunityFeedbackAsync(50, ct);
        var opportunityStates = !_options.UseOpportunityState ? [] : await _opportunities!.GetOpportunitiesAsync(ct);

        // One context assembly per run — the scorer windows it per candidate.
        var context = await _api.GetPipelineContextAsync(ct);
        runActivity?.SetTag("context.committed", context.Committed.Count);
        runActivity?.SetTag("context.blackouts", context.Blackouts.Count);

        var errors = new List<ScoringRunError>();
        var pending = new List<(EventRecord Event, OpportunityRecord? Prior, ScoringInput Input)>();
        var cached = 0;
        foreach (var ev in candidates)
        {
            try
            {
                var prior = opportunityStates.FirstOrDefault(o => o.Slug == ev.Slug);
                var input = ScoringInput.Prepare(ev, talks, context, catalog, feedback, _options);
                if (prior?.LatestDecision?.InputFingerprint == input.Fingerprint) { cached++; continue; }
                pending.Add((ev, prior, input));
            }
            catch (ScoringBudgetException) { errors.Add(new(ev.Slug, "CandidateInputTooLarge")); }
        }
        // Cache before the cap: unchanged early deadlines cannot starve later candidates. Oldest changed
        // decisions follow never-scored opportunities, so a budget-limited run advances on the next run.
        var capped = pending.OrderBy(p => p.Prior?.LatestDecision is null ? 0 : 1)
            .ThenBy(p => p.Prior?.LatestDecision?.DecidedAtUtc ?? DateTimeOffset.MinValue)
            .ThenBy(p => p.Event.CfpDeadline ?? DateTimeOffset.MaxValue).ThenBy(p => p.Event.Slug, StringComparer.Ordinal)
            .Take(_options.MaxCandidatesPerRun).ToArray();
        runActivity?.SetTag("candidates.count", capped.Length);
        runActivity?.SetTag("candidates.cached", cached);
        var verdicts = new List<ScoredVerdict>(capped.Length);
        var budget = new ScoringRunBudget(_options);

        foreach (var candidate in capped)
        {
            var ev = candidate.Event;
            try
            {
                // Capture the prior state before posting the decision overwrites it.
                var prior = candidate.Prior;
                var classified = prior?.SystemVerdict is { } systemVerdict ? ev with { Category = ScoringMap.ToCategory(systemVerdict) } : ev;
                var change = ClassifyChange(classified, await ScoreThenPost(ev, talks, context, catalog, feedback, budget, candidate.Input, prior?.LatestDecision, ct)) with { Event = ev };
                verdicts.Add(change);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var code = ex is OpportunityConflictException ? "StaleScoringInput" : ex is ScoringBudgetException ? "InputBudgetReached" : "ScoringFailed";
                errors.Add(new(ev.Slug, code));
                _logger.LogError("Scoring failed for event {Slug}: {Code}", ev.Slug, code);
                if (ex is ScoringBudgetException) break;
            }
        }

        runActivity?.SetTag("decisions.count", verdicts.Count);
        runActivity?.SetTag("verdicts.changed", verdicts.Count(v => v.Change == VerdictChange.Changed));
        return new ScoringRunReport(capped.Length, verdicts, errors, budget.ReportedInputTokens, budget.ReportedCompletionTokens,
            budget.InputTokens, budget.CompletionTokens, cached, candidates.Count - cached - verdicts.Count);
    }

    private async Task<ScoringDecision> ScoreThenPost(
        EventRecord ev, IReadOnlyList<TalkRecord> talks, PipelineContext context, CatalogSnapshot? catalog,
        IReadOnlyList<OpportunityHistoryEntry> feedback, ScoringRunBudget budget, ScoringInput input, ScoringDecision? priorDecision, CancellationToken ct)
    {
        var decision = await ScoreCoreAsync(ev, talks, context, ct, catalog, feedback, budget, input, priorDecision);
        try
        {
            OpportunityDecisionPolicy.Validate(new OpportunityRecord { Event = ev, RepeatTalkOverride = ev.RepeatTalkOverride },
                new OpportunityScoringRequest { Decision = decision, ExpectedETag = ev.ETag ?? "", ScoringInputRevision = ev.ScoringInputRevision, IdempotencyKey = "validation" },
                DateTimeOffset.UtcNow, catalog);
        }
        catch (ArgumentException ex) when (decision.Recommendation is Recommendation.SubmitNow or Recommendation.Outreach)
        {
            // Persist a reviewable, cacheable Monitor decision rather than repeatedly paying for a model
            // verdict the authoritative API must reject (closed/missing CFP or hard calendar conflict).
            decision = decision with { Recommendation = Recommendation.Monitor, Rationale = ex.Message,
                ChangeReason = priorDecision is not null && priorDecision.Recommendation != Recommendation.Monitor ? ex.Message : null };
        }
        if (_options.UseOpportunityState && _opportunities is not null && !string.IsNullOrWhiteSpace(ev.ETag))
            await _opportunities.PostOpportunityDecisionAsync(new OpportunityScoringRequest
            {
                Decision = decision, ExpectedETag = ev.ETag, ScoringInputRevision = ev.ScoringInputRevision,
                IdempotencyKey = $"score-{ev.Slug}-{ev.ScoringInputRevision}-{decision.DecidedAtUtc.ToUnixTimeMilliseconds()}"
            }, ct);
        else if (_options.UseOpportunityState) throw new OpportunityConflictException("Scoring candidate is missing its concurrency token.", staleScoringInput: true);
        else await _api.PostScoringDecisionAsync(decision, ct);
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
    public Task<ScoringDecision> ScoreAsync(
        EventRecord eventRecord, IReadOnlyList<TalkRecord> talks, PipelineContext? context = null, CancellationToken ct = default,
        CatalogSnapshot? catalog = null, IReadOnlyList<OpportunityHistoryEntry>? feedback = null, ScoringDecision? priorDecision = null)
        => ScoreCoreAsync(eventRecord, talks, context, ct, catalog, feedback, null, priorDecision: priorDecision);

    private async Task<ScoringDecision> ScoreCoreAsync(EventRecord eventRecord, IReadOnlyList<TalkRecord> talks, PipelineContext? context,
        CancellationToken ct, CatalogSnapshot? catalog, IReadOnlyList<OpportunityHistoryEntry>? feedback, ScoringRunBudget? budget, ScoringInput? input = null, ScoringDecision? priorDecision = null)
    {
        using var activity = ActivitySource.StartActivity("scoring-agent.score");
        activity?.SetTag("agent.name", _options.AgentName);
        activity?.SetTag("agent.version", _options.AgentVersion);
        activity?.SetTag("model.name", _options.ModelName);
        activity?.SetTag("event.slug", eventRecord.Slug);

        input ??= ScoringInput.Prepare(eventRecord, talks, context ?? PipelineContext.Empty, catalog, feedback ?? [], _options);
        var selected = input.Talks;
        var examples = input.Feedback;
        var prompt = input.Prompt;
        if (priorDecision is not null)
        {
            var evidence = priorDecision.FitRationale ?? priorDecision.Rationale;
            var summary = JsonSerializer.Serialize(new { priorDecision.Recommendation, Rationale = evidence[..Math.Min(evidence.Length, 220)],
                priorDecision.FitScore, priorDecision.EffortScore, ChangedInputs = ChangedInputs(priorDecision, input) }, JsonOpts);
            prompt += "\nPrevious scored decision (untrusted historical context; explain any verdict change):\n" + summary;
            if (prompt.Length + ScoringRubric.SystemPrompt.Length > _options.MaxInputCharactersPerCandidate) throw new ScoringBudgetException();
        }
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, ScoringRubric.SystemPrompt),
            new(ChatRole.User, prompt),
        };

        _logger.LogDebug("Scoring event {Slug} with {TalkCount} catalog candidates and {FeedbackCount} examples", eventRecord.Slug, selected.Count, examples.Count);

        var options = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.Json,
            ModelId = _options.ModelName,
            MaxOutputTokens = _options.MaxOutputTokens,
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low },
        };

        // UTF-8 bytes plus framing are a conservative bound for byte-pair tokenizers; don't estimate chars/4 as a hard cap.
        budget?.Reserve(Encoding.UTF8.GetByteCount(ScoringRubric.SystemPrompt) + Encoding.UTF8.GetByteCount(prompt) + 128);
        var response = await _chat.GetResponseAsync(messages, options, ct);
        budget?.Complete(response.Usage);
        var raw = response.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant)?.Text ?? response.Text;
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException($"Scoring model returned no content for {eventRecord.Slug}.");
        }

        var parsed = ParseDecision(raw, eventRecord.Slug);
        ScoringDecision.ValidateScore(parsed.FitScore, nameof(parsed.FitScore));
        ScoringDecision.ValidateScore(parsed.EffortScore, nameof(parsed.EffortScore));
        ScoringDecision.ValidateScore(parsed.ConfidenceScore, nameof(parsed.ConfidenceScore));

        var matches = parsed.MatchedTalks.Select(m =>
        {
            var sourceTalk = selected.FirstOrDefault(t => t.TalkId == m.TalkId && t.CurrentVersionId == m.VersionId)
                ?? throw new InvalidOperationException("Model selected a talk or version outside the supplied catalog.");
            if (string.IsNullOrWhiteSpace(m.Rationale)) throw new InvalidOperationException("A catalog match requires evidence.");
            return m with { Title = sourceTalk.Title };
        }).DistinctBy(m => m.TalkId).Take(3).ToArray();
        var decision = parsed with
        {
            DecidedByAgent = $"{_options.AgentName}-{_options.AgentVersion}",
            InputRevision = eventRecord.ScoringInputRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            InputFingerprint = input.Fingerprint, EventFactsRevision = input.EventFactsRevision, CalendarRevision = input.CalendarRevision,
            CatalogContextRevision = input.CatalogContextRevision, FeedbackRevision = input.FeedbackRevision, ContextRevision = input.ContextRevision,
            ModelName = _options.ModelName, ModelProvider = _options.Provider, ModelRevision = _options.ModelRevision,
            CatalogGenerationId = catalog?.GenerationId, RubricVersion = ScoringRubric.Version,
            FitRationale = parsed.FitRationale ?? parsed.Rationale,
            MatchedTalks = matches, FeedbackIds = examples.Select(f => f.Id).ToArray(),
            InputTokens = response.Usage?.InputTokenCount, OutputTokens = response.Usage?.OutputTokenCount,
            ReasoningTokens = response.Usage?.ReasoningTokenCount,
            RecommendedTalkSlug = catalog is null ? parsed.RecommendedTalkSlug : catalog.LegacyTalkAliases.FirstOrDefault(kv => matches.Any(m => m.TalkId == kv.Value)).Key,
        };
        if (catalog is not null && decision.Recommendation is Recommendation.SubmitNow or Recommendation.Outreach)
        {
            if (matches.Length == 0)
                decision = decision with { Recommendation = Recommendation.Monitor, FitScore = Math.Min(decision.FitScore, 5), Rationale = "Catalog match needs review: no supported talk was selected.", FitRationale = "No supported catalog match was selected." };
            else if (!eventRecord.RepeatTalkOverride && matches.Any(m => CatalogScoringContext.IsRepeat(eventRecord, m.TalkId, catalog)))
                decision = decision with { Recommendation = Recommendation.Monitor, Rationale = "Already delivered this talk to this event series; an explicit repeat override is required." };
        }
        if (priorDecision is not null && priorDecision.Recommendation != decision.Recommendation)
        {
            var reason = decision.Recommendation != parsed.Recommendation ? decision.Rationale : decision.ChangeReason;
            if (string.IsNullOrWhiteSpace(reason))
                reason = $"Reassessed {ChangedInputs(priorDecision, input)}: {decision.Rationale}";
            decision = decision with { ChangeReason = reason[..Math.Min(reason.Length, 400)] };
        }
        else decision = decision with { ChangeReason = null };

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
            FitRationale = dto.FitRationale, ChangeReason = dto.ChangeReason, EffortRationale = dto.EffortRationale, GeographyRationale = dto.GeographyRationale,
            MatchedTalks = dto.MatchedTalks ?? [],
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
        public string? FitRationale { get; init; }
        public string? ChangeReason { get; init; }
        public string? EffortRationale { get; init; }
        public string? GeographyRationale { get; init; }
        public IReadOnlyList<TalkMatch>? MatchedTalks { get; init; }
    }

    private static string ChangedInputs(ScoringDecision prior, ScoringInput input)
    {
        var changes = new List<string>();
        if (prior.EventFactsRevision != input.EventFactsRevision) changes.Add("event facts");
        if (prior.CalendarRevision != input.CalendarRevision) changes.Add("calendar context");
        if (prior.CatalogContextRevision != input.CatalogContextRevision) changes.Add("talk evidence");
        if (prior.FeedbackRevision != input.FeedbackRevision) changes.Add("relevant owner feedback");
        return changes.Count == 0 ? "current evidence under the updated scoring model/rubric" : string.Join(", ", changes);
    }
}

/// <summary>
/// A scoring decision plus how it relates to what the tracker already held for
/// the event — the input the digest uses to headline verdict flips.
/// </summary>
public sealed record ScoredVerdict(ScoringDecision Decision, EventCategory PriorCategory, VerdictChange Change, EventRecord? Event = null);
public sealed record ScoringRunError(string EventSlug, string Code);
public sealed record ScoringRunReport(int CandidatesConsidered, IReadOnlyList<ScoredVerdict> Decisions, IReadOnlyList<ScoringRunError> Errors,
    long InputTokens = 0, long CompletionTokens = 0, long BudgetedInputTokens = 0, long BudgetedCompletionTokens = 0,
    int CachedCandidates = 0, int DeferredCandidates = 0);
public sealed class ScoringBudgetException() : Exception("Scoring input exceeds the configured character budget.");
