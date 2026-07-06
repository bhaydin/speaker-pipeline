using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Agents.Discovery;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Agents.Discovery.Tests;

public class DiscoveryAgentTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private const string Agent = "discovery-agent-v1";

    // --- Reconcile: new events ----------------------------------------------

    [Fact]
    public void Reconcile_new_event_enters_as_monitor_candidate()
    {
        var extracted = new ExtractedEvent
        {
            IsEvent = true,
            EventName = "Northwoods Tech Summit 2027",
            EventType = EventType.Conference,
            Location = "Minocqua, WI",
            Format = EventFormat.InPerson,
            CfpStatus = CfpStatus.Open,
            CfpDeadline = Now.AddDays(30),
            Confidence = 9,
        };

        var (upsert, summary, isNew) = DiscoveryAgent.Reconcile(
            existing: null, extracted, "northwoods-tech-summit-2027", SourceSeenOn.Sessionize, Now, Agent);

        Assert.True(isNew);
        Assert.Equal("new", summary);
        Assert.NotNull(upsert);
        Assert.Equal(EventCategory.Monitor, upsert!.Category);          // enters the scoring pool
        Assert.Equal(CfpStatus.Open, upsert.CfpStatus);
        Assert.Equal(SourceSeenOn.Sessionize, upsert.SourceSeenOn);
        Assert.Equal(Agent, upsert.DiscoveredByAgent);
        Assert.Equal(Now, upsert.LastVerifiedUtc);
    }

    // --- Reconcile: existing events -----------------------------------------

    [Fact]
    public void Reconcile_do_not_resurface_is_skipped()
    {
        var existing = Event("x", EventCategory.Pass) with { DoNotResurface = true };
        var extracted = new ExtractedEvent { IsEvent = true, EventName = "X", CfpStatus = CfpStatus.Open, Confidence = 9 };

        var (upsert, summary, _) = DiscoveryAgent.Reconcile(existing, extracted, "x", SourceSeenOn.Sessionize, Now, Agent);

        Assert.Null(upsert);
        Assert.Contains("do-not-resurface", summary);
    }

    [Fact]
    public void Reconcile_no_new_information_is_idempotent()
    {
        var existing = Event("x", EventCategory.Monitor) with { CfpStatus = CfpStatus.Open, Location = "Duluth" };
        // Extraction with no fresh signal (Unknown status, null location).
        var extracted = new ExtractedEvent { IsEvent = true, EventName = "X", CfpStatus = CfpStatus.Unknown, Location = null, Confidence = 8 };

        var (upsert, summary, _) = DiscoveryAgent.Reconcile(existing, extracted, "x", SourceSeenOn.Sessionize, Now, Agent);

        Assert.Null(upsert);
        Assert.Equal("no change", summary);
    }

    [Fact]
    public void Reconcile_cfp_status_change_updates_without_touching_category()
    {
        var existing = Event("x", EventCategory.SubmitNow) with { CfpStatus = CfpStatus.Open };
        var extracted = new ExtractedEvent { IsEvent = true, EventName = "X", CfpStatus = CfpStatus.Closed, Confidence = 9 };

        var (upsert, summary, isNew) = DiscoveryAgent.Reconcile(existing, extracted, "x", SourceSeenOn.Sessionize, Now, Agent);

        Assert.False(isNew);
        Assert.NotNull(upsert);
        Assert.Equal(CfpStatus.Closed, upsert!.CfpStatus);
        Assert.Equal(EventCategory.SubmitNow, upsert.Category);   // scoring's decision preserved
        Assert.Contains("CfpStatus", summary);
    }

    [Fact]
    public void Reconcile_unknown_extraction_never_overwrites_known_values()
    {
        var existing = Event("x", EventCategory.Monitor) with { CfpStatus = CfpStatus.Open, Location = "Duluth" };
        var extracted = new ExtractedEvent { IsEvent = true, EventName = "X", CfpStatus = CfpStatus.Unknown, Location = null, Confidence = 8 };

        var (upsert, _, _) = DiscoveryAgent.Reconcile(existing, extracted, "x", SourceSeenOn.Sessionize, Now, Agent);

        Assert.Null(upsert); // nothing to write — the known Open/Duluth values stand
    }

    [Fact]
    public void Reconcile_writes_new_event_under_requested_category()
    {
        var extracted = new ExtractedEvent
        {
            IsEvent = true, EventName = "Maybe Conf 2027", CfpStatus = CfpStatus.Open, Confidence = 3,
        };

        var (upsert, _, isNew) = DiscoveryAgent.Reconcile(
            existing: null, extracted, "maybe-conf-2027", SourceSeenOn.Sessionize, Now, Agent,
            newCategory: EventCategory.Quarantine);

        Assert.True(isNew);
        Assert.NotNull(upsert);
        Assert.Equal(EventCategory.Quarantine, upsert!.Category); // held for review, not a scoring candidate
    }

    [Fact]
    public async Task RunAsync_drops_invalid_candidate_without_extracting_or_upserting()
    {
        var api = new RecordingApiClient();
        var agent = new DiscoveryAgent(
            new ThrowingChatClient(),
            api,
            [new StubSourceAdapter(new DiscoveryCandidate
            {
                Url = "https://example.org/bad",
                Source = SourceSeenOn.Direct,
            })],
            Options.Create(new DiscoveryOptions { AgentName = "discovery-agent", AgentVersion = "test" }),
            Options.Create(new SearchOptions()),
            NullLogger<DiscoveryAgent>.Instance);

        var report = await agent.RunAsync();

        Assert.Empty(report.Changed);
        Assert.Empty(report.Quarantined);
        Assert.Equal(1, report.Funnel.Dropped["invalid_candidate"]);
        Assert.Empty(api.UpsertedEvents);
    }

    // --- ClassifyDrop --------------------------------------------------------

    [Theory]
    [InlineData("skipped: do-not-resurface", "do_not_resurface")]
    [InlineData("skipped: cfp closed", "cfp_closed")]
    [InlineData("no change", "unchanged")]
    public void ClassifyDrop_maps_reconcile_summaries_to_reason_codes(string summary, string expected)
        => Assert.Equal(expected, DiscoveryAgent.ClassifyDrop(summary));

    // --- Slugify -------------------------------------------------------------

    [Theory]
    [InlineData("Northwoods Tech Summit 2027", "northwoods-tech-summit-2027")]
    [InlineData("  Great Lakes Cloud Conf  ", "great-lakes-cloud-conf")]
    [InlineData("AI & Agents!", "ai-agents")]
    [InlineData("Driftless.AI.Days", "driftless-ai-days")]
    public void Slugify_produces_stable_kebab_slugs(string name, string expected)
        => Assert.Equal(expected, DiscoveryAgent.Slugify(name));

    // --- ParseExtracted ------------------------------------------------------

    [Fact]
    public void ParseExtracted_reads_valid_json()
    {
        var e = DiscoveryAgent.ParseExtracted("""
            {"isEvent":true,"eventName":"X","cfpStatus":"Open","confidence":7}
            """);
        Assert.True(e.IsEvent);
        Assert.Equal(CfpStatus.Open, e.CfpStatus);
    }

    [Fact]
    public void ParseExtracted_tolerates_json_fences()
    {
        var e = DiscoveryAgent.ParseExtracted("```json\n{\"isEvent\":true,\"eventName\":\"X\"}\n```");
        Assert.True(e.IsEvent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("not json at all")]
    [InlineData("{\"isEvent\": tru")]
    public void ParseExtracted_returns_non_event_on_unusable_output(string content)
        => Assert.False(DiscoveryAgent.ParseExtracted(content).IsEvent);

    // --- HTML normalization --------------------------------------------------

    [Fact]
    public void Normalize_strips_scripts_tags_and_decodes_entities()
    {
        var html = "<html><head><style>.x{}</style></head><body><script>evil()</script>" +
                   "<h1>Contoso&nbsp;Conf</h1><p>Submit &amp; win</p></body></html>";

        var text = WatchlistSource.Normalize(html);

        Assert.DoesNotContain("evil()", text);
        Assert.DoesNotContain("<", text);
        Assert.Contains("Contoso", text);
        Assert.Contains("Submit & win", text);
    }

    [Fact]
    public void Normalize_prefers_main_region_over_chrome()
    {
        var html = "<html><body><nav>Home About <a>Sponsors</a></nav>" +
                   "<main><h1>Contoso Conf</h1><p>CFP closes June 1</p></main>" +
                   "<footer>Cookie banner accept all</footer></body></html>";

        var text = WatchlistSource.Normalize(html);

        Assert.Contains("Contoso Conf", text);
        Assert.Contains("CFP closes June 1", text);
        Assert.DoesNotContain("Cookie banner", text);   // footer chrome dropped
        Assert.DoesNotContain("Sponsors", text);         // nav chrome dropped
    }

    [Fact]
    public void Normalize_falls_back_to_whole_page_without_landmarks()
    {
        var html = "<html><body><div><h1>Northwoods Summit</h1></div></body></html>";

        Assert.Contains("Northwoods Summit", WatchlistSource.Normalize(html));
    }

    [Fact]
    public void Normalize_caps_length_at_24k()
    {
        var html = "<main>" + new string('x', 40_000) + "</main>";

        Assert.Equal(24_000, WatchlistSource.Normalize(html).Length);
    }

    private static EventRecord Event(string slug, EventCategory category) => new()
    {
        Slug = slug,
        Name = slug,
        EventType = EventType.Conference,
        Category = category,
        Priority = Priority.Medium,
    };

    private sealed class StubSourceAdapter(DiscoveryCandidate candidate) : ISourceAdapter
    {
        public Task<IReadOnlyList<DiscoveryCandidate>> FetchAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DiscoveryCandidate>>([candidate]);
    }

    private sealed class ThrowingChatClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("fake", new Uri("https://fake.invalid"), "fake-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Extraction should not run for invalid candidates.");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class RecordingApiClient : ISpeakerPipelineApiClient
    {
        public List<EventRecord> UpsertedEvents { get; } = [];

        public Task<IReadOnlyList<EventRecord>> GetEventsAsync(IReadOnlyList<EventCategory>? categories = null, TimeSpan? deadlineWindow = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EventRecord>>([]);

        public Task<EventRecord?> GetEventAsync(string slug, CancellationToken ct = default) => Task.FromResult<EventRecord?>(null);

        public Task<EventRecord> UpsertEventAsync(EventRecord record, CancellationToken ct = default)
        {
            UpsertedEvents.Add(record);
            return Task.FromResult(record);
        }

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
        public Task<IReadOnlyList<NotificationLogRecord>> GetNotificationsAsync(string period, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<NotificationLogRecord>>([]);
        public Task<NotificationLogRecord> LogNotificationAsync(NotificationLogRecord record, CancellationToken ct = default) => Task.FromResult(record);
    }
}
