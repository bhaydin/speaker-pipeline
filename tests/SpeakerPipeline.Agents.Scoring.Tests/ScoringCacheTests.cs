using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Agents.Scoring.Tests;

public sealed class ScoringCacheTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly EventRecord Event = new()
    {
        Slug = "agentops-2027", Name = "AgentOps Operations", Notes = "Tracing and evaluations in production agents",
        EventType = EventType.Conference, Category = EventCategory.Monitor, Priority = Priority.Medium,
        CfpDeadline = Now.AddDays(30), EventDateStart = Now.AddDays(100), ETag = "first", ScoringInputRevision = 1
    };
    private static readonly CatalogSource Source = new("source", "talk.yaml", "commit", "hash");
    private static readonly CatalogSnapshot Catalog = new()
    {
        GenerationId = "first", Talks = [new() { TalkId = "agentops", CurrentVersionId = "v1", Title = "AgentOps", Abstract = "Tracing and evaluations", Source = Source }]
    };
    private static ScoringInput Input(EventRecord? ev = null, PipelineContext? context = null, CatalogSnapshot? catalog = null,
        IReadOnlyList<OpportunityHistoryEntry>? feedback = null, ScoringAgentOptions? options = null)
        => ScoringInput.Prepare(ev ?? Event, [], context ?? new() { AsOfUtc = Now }, catalog ?? Catalog, feedback ?? [], options ?? new());

    [Fact]
    public void Cache_ignores_run_time_source_commit_generation_and_our_previous_verdict()
    {
        var initial = Input();
        var unchanged = Input(Event with { ETag = "new", ScoringInputRevision = 9, Category = EventCategory.SubmitNow, Priority = Priority.High, DecidedByAgent = "scorer", LastVerifiedUtc = Now },
            new() { AsOfUtc = Now.AddDays(1) }, Catalog with { GenerationId = "new", SyncedAtUtc = Now.AddDays(1), Talks = [Catalog.Talks[0] with { Source = Source with { CommitSha = "new" } }] });
        Assert.Equal(initial.Fingerprint, unchanged.Fingerprint);
    }

    [Fact]
    public void Relevant_fact_calendar_model_rubric_context_and_substantive_talk_changes_invalidate()
    {
        var initial = Input();
        Assert.NotEqual(initial.Fingerprint, Input(Event with { Location = "London" }).Fingerprint);
        Assert.NotEqual(initial.Fingerprint, Input(Event with { CfpDeadline = Now.AddDays(10) }).Fingerprint);
        Assert.NotEqual(initial.Fingerprint, Input(context: new() { AsOfUtc = Now.AddDays(20) }).Fingerprint);
        Assert.NotEqual(initial.Fingerprint, Input(context: new() { AsOfUtc = Now, Blackouts = [new(Event.EventDateStart!.Value, Event.EventDateStart.Value.AddDays(1), "Family", BlackoutHardness.Hard)] }).Fingerprint);
        Assert.NotEqual(initial.Fingerprint, Input(options: new() { ModelRevision = "new-provider-version" }).Fingerprint);
        Assert.NotEqual(initial.Fingerprint, Input(catalog: Catalog with { Talks = [Catalog.Talks[0] with { Abstract = "Substantive new abstract", CurrentVersionId = "v2" }] }).Fingerprint);
        Assert.Equal(initial.Fingerprint, Input(context: new() { AsOfUtc = Now, Blackouts = [new(Now.AddYears(3), Now.AddYears(3).AddDays(1), "Far away", BlackoutHardness.Hard)] }).Fingerprint);
    }

    [Fact]
    public void Unrelated_feedback_does_not_fill_slots_or_change_other_candidates()
    {
        var unrelated = Feedback("other", "Gardening Summit 2027", "Horticulture", "gardening");
        var relevant = Feedback(Event.Slug, Event.Name, "AgentOps", "agentops");
        Assert.Empty(Input(feedback: [unrelated]).Feedback);
        Assert.Equal(Input().Fingerprint, Input(feedback: [unrelated]).Fingerprint);
        Assert.Single(Input(feedback: [unrelated, relevant]).Feedback);
        Assert.NotEqual(Input().Fingerprint, Input(feedback: [unrelated, relevant]).Fingerprint);
    }

    [Fact]
    public void Event_url_correction_invalidates_when_it_changes_full_catalog_repeat_result()
    {
        var catalog = Catalog with { Deliveries = [new()
        {
            DeliveryId = "past", TalkId = "agentops", EventName = "Previous brand name",
            EventUrl = "https://example.org/conference/2025", Status = "completed", Source = Source,
        }] };
        var before = Input(Event with { EventUrl = "https://other.example.org/" }, catalog: catalog);
        var after = Input(Event with { EventUrl = "https://example.org/conference/2027" }, catalog: catalog);

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
        Assert.NotEqual(before.CatalogContextRevision, after.CatalogContextRevision);
        Assert.Contains("\"PreviouslyDeliveredAtEventSeries\":true", after.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Delivery_beyond_prompt_history_limit_invalidates_when_it_changes_repeat_result()
    {
        var recent = Enumerable.Range(0, 24).Select(i => new CatalogDelivery
        {
            DeliveryId = $"recent-{i}", TalkId = "agentops", EventName = $"Unrelated venue {i}",
            Date = Now.AddDays(-i), Status = "completed", Source = Source,
        }).ToArray();
        var old = new CatalogDelivery
        {
            DeliveryId = "older-than-history-limit", TalkId = "agentops", EventName = "Historic series branding", EventSeriesId = Event.Slug,
            Date = Now.AddYears(-2), Status = "planned", Source = Source,
        };
        var before = Input(catalog: Catalog with { Deliveries = [.. recent, old] });
        var after = Input(catalog: Catalog with { Deliveries = [.. recent, old with { Status = "completed" }] });
        var unrelated = Input(catalog: Catalog with { Deliveries = [.. recent, old with { EventName = "Unrelated older venue", EventSeriesId = null, Status = "completed" }] });

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
        Assert.NotEqual(before.CatalogContextRevision, after.CatalogContextRevision);
        Assert.Equal(before.Fingerprint, unrelated.Fingerprint);
        Assert.DoesNotContain(old.EventName, after.Prompt, StringComparison.Ordinal);
        Assert.Contains("\"PreviouslyDeliveredAtEventSeries\":true", after.Prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(3, 81920, 3, 2)]
    [InlineData(20, 8192, 2, 2)]
    public async Task Filtering_before_cap_and_budget_makes_later_candidates_progress(int cap, int completionBudget, int firstCount, int secondCount)
    {
        var api = new FakeApiClient();
        api.Candidates.AddRange(Enumerable.Range(0, 5).Select(i => Event with { Slug = $"event-{i}", CfpDeadline = Now.AddDays(30 + i) }));
        var opportunities = new MemoryOpportunities(api);
        var chat = new FakeChatClient();
        var scorer = new ScoringAgent(chat, api, Options.Create(new ScoringAgentOptions
        {
            UseOpportunityState = true, MaxCandidatesPerRun = cap, MaxCompletionTokensPerRun = completionBudget
        }), NullLogger<ScoringAgent>.Instance, opportunities: opportunities);
        var first = await scorer.RunWithReportAsync();
        var second = await scorer.RunWithReportAsync();
        Assert.Equal(firstCount, first.Decisions.Count);
        Assert.Equal(secondCount, second.Decisions.Count);
        Assert.Equal(firstCount, second.CachedCandidates);
        Assert.Empty(first.Decisions.Select(v => v.Decision.EventSlug).Intersect(second.Decisions.Select(v => v.Decision.EventSlug)));
        await scorer.RunWithReportAsync();
        var idle = await scorer.RunWithReportAsync();
        Assert.Empty(idle.Decisions);
        Assert.Equal(5, idle.CachedCandidates);
        Assert.Equal(5, chat.CapturedMessages.Count);
        var decision = opportunities.Records.Values.First().LatestDecision!;
        Assert.NotNull(decision.InputFingerprint);
        Assert.NotNull(decision.CalendarRevision);
        Assert.NotNull(decision.CatalogContextRevision);
        Assert.NotNull(decision.FeedbackRevision);
        Assert.Equal(ScoringRubric.Version, decision.RubricVersion);

        api.Candidates[0] = api.Candidates[0] with { Notes = "Changed audience requirements" };
        var changed = await scorer.RunWithReportAsync();
        Assert.Equal("event-0", Assert.Single(changed.Decisions).Decision.EventSlug);
        Assert.Equal(4, changed.CachedCandidates);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("https://example.org/cfp", true)]
    public async Task Unactionable_model_verdict_is_persisted_as_monitor_and_cached(string? cfpUrl, bool conflict)
    {
        var api = new FakeApiClient();
        api.Candidates.Add(Event with { CfpUrl = cfpUrl, FamilyConflictFlag = conflict });
        var opportunities = new MemoryOpportunities(api);
        var chat = new FakeChatClient();
        chat.Enqueue("""{"recommendation":"SubmitNow","rationale":"Model ignored invalid CFP/calendar","fitScore":8,"effortScore":3,"confidenceScore":8}""");
        var scorer = new ScoringAgent(chat, api, Options.Create(new ScoringAgentOptions { UseOpportunityState = true }),
            NullLogger<ScoringAgent>.Instance, opportunities: opportunities);
        var run = await scorer.RunWithReportAsync();
        Assert.Equal(Recommendation.Monitor, Assert.Single(run.Decisions).Decision.Recommendation);
        Assert.Empty(run.Errors);
        Assert.Empty((await scorer.RunWithReportAsync()).Decisions);
        Assert.Single(chat.CapturedMessages);
    }

    private static OpportunityHistoryEntry Feedback(string slug, string name, string title, string talkId) => new()
    {
        Id = slug, EventSlug = slug, EventName = name, Kind = "Feedback", Actor = "owner", AtUtc = Now, Agree = false, ReasonCode = FeedbackReason.WrongAudience,
        Decision = new() { EventSlug = slug, Recommendation = Recommendation.SubmitNow, Rationale = "Prior", FitScore = 8, EffortScore = 3, ConfidenceScore = 7,
            MatchedTalks = [new(talkId, "v1", title, "Prior evidence")] }
    };

    private sealed class MemoryOpportunities(FakeApiClient api) : IOpportunityApiClient
    {
        public Dictionary<string, OpportunityRecord> Records { get; } = api.Candidates.ToDictionary(e => e.Slug, e => new OpportunityRecord { Event = e, ETag = e.ETag! });
        public Task<IReadOnlyList<OpportunityRecord>> GetOpportunitiesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<OpportunityRecord>>(Records.Values.ToArray());
        public Task<OpportunityRecord?> GetOpportunityAsync(string slug, CancellationToken ct = default) => Task.FromResult(Records.GetValueOrDefault(slug));
        public Task<IReadOnlyList<OpportunityHistoryEntry>> GetOpportunityFeedbackAsync(int limit = 20, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<OpportunityHistoryEntry>>([]);
        public Task<IReadOnlyList<OpportunityHistoryEntry>> GetOpportunityHistoryAsync(string slug, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<OpportunityHistoryEntry>>([]);
        public Task<OpportunityRecord> PostOpportunityDecisionAsync(OpportunityScoringRequest request, CancellationToken ct = default)
        {
            var updated = Records[request.Decision.EventSlug] with { LatestDecision = request.Decision, SystemVerdict = request.Decision.Recommendation };
            Records[updated.Slug] = updated;
            return Task.FromResult(updated);
        }
        public Task<OpportunityRecord> ApplyOpportunityActionAsync(string slug, OpportunityActionRequest request, string expectedETag, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
