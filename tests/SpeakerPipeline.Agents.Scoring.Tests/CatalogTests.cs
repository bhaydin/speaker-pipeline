using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Catalog;
using SpeakerPipeline.Core;
using SpeakerPipeline.Notifications;

namespace SpeakerPipeline.Agents.Scoring.Tests;

public sealed class CatalogTests
{
    [Theory]
    [InlineData("2026-03-08T08:00:00Z", "scheduled-2026-03-08")]
    [InlineData("2026-03-08T07:00:00Z", null)]
    [InlineData("2026-11-01T09:00:00Z", "scheduled-2026-11-01")]
    [InlineData("2026-11-01T08:00:00Z", null)]
    [InlineData("2026-10-16T08:00:00Z", "scheduled-2026-10-16")]
    [InlineData("2026-10-16T08:45:00Z", "scheduled-2026-10-16")]
    public void Nightly_schedule_follows_Chicago_three_am_and_reuses_daily_id(string utc, string? expected)
        => Assert.Equal(expected, CatalogSchedule.NightlyDeliveryId(DateTimeOffset.Parse(utc)));

    private const string TalkYaml = """
        schema_version: 1
        id: agentops
        title: AgentOps original
        description: Practical agent operations, observability and evaluation for developers.
        status: active
        topics: [agents, observability]
        audiences: [developers]
        formats: [conference-session, webinar]
        content:
          source: content/talk.md
        updated: 2026-08-01
        """;
    private static RepositoryFile File(string path, string text) => new(path, CatalogBuilder.ContentHash(text), text);
    private static RepositorySnapshot Private(params RepositoryFile[] extra) => new("bhaydin/bhaydin-talks", "private-head", [File("talks/agentops/talk.yaml", TalkYaml), .. extra]);
    private static RepositorySnapshot Public => new("bhaydin/Presentations", "public-head", []);
    private static CatalogSnapshot Build(RepositorySnapshot repo, CatalogSnapshot? previous = null)
        => CatalogBuilder.Build(repo, Public, previous ?? new(), new Dictionary<string, CatalogExtraction>(), DateTimeOffset.UtcNow);

    [Fact]
    public void Repeated_deliveries_are_one_talk_and_newest_abstract_wins_without_erasing_history()
    {
        var repo = Private(
            File("engagements/2026/summit/engagement.yaml", """
                id: summit
                talk_id: agentops
                event: Summit
                date: 2026-10-01
                status: planned
                session_title: AgentOps for Real
                """),
            File("engagements/2026/summit/sources/session-abstract.md", "# AgentOps for Real\n\n## Abstract\nConcrete production agent evaluation, tracing and regression testing for .NET developers."),
            File("engagements/2026/old/engagement.yaml", "id: old\ntalk_id: agentops\nevent: Earlier\ndate: 2026-01-01\nstatus: completed\n"));
        var catalog = Build(repo);
        Assert.Single(catalog.Talks);
        Assert.Equal(2, catalog.Deliveries.Count);
        Assert.Equal(2, catalog.Versions.Count);
        Assert.Equal("AgentOps for Real", catalog.Talks[0].Title);
        Assert.Contains("AgentOps original", catalog.Talks[0].PriorTitles);
        Assert.Equal("planned", catalog.Deliveries.Single(d => d.DeliveryId == "summit").Status);
        Assert.Equal("unknown", catalog.Deliveries.Single(d => d.DeliveryId == "old").DatePrecision);
    }

    [Fact]
    public void Invalid_or_removed_source_preserves_previous_records_and_reviewed_aliases()
    {
        var first = Build(Private());
        var reviewed = CatalogReview.Apply(first, new CatalogReviewRequest { ExpectedGenerationId = first.GenerationId, TalkId = "agentops", LegacyTalkSlug = "agentops-real-world" });
        var invalid = Build(new RepositorySnapshot("bhaydin/bhaydin-talks", "new", [File("talks/agentops/talk.yaml", "id: wrong\ntitle: Invalid")]), reviewed);
        Assert.Equal("agentops", Assert.Single(invalid.Talks).TalkId);
        Assert.True(invalid.Talks[0].NeedsReview);
        Assert.Equal("agentops", invalid.LegacyTalkAliases["agentops-real-world"]);
        var removed = Build(new RepositorySnapshot("bhaydin/bhaydin-talks", "newer", []), invalid);
        Assert.False(removed.Talks[0].Eligible);
        Assert.Single(removed.Versions);
    }

    [Fact]
    public void Stub_does_not_invent_abstract_and_public_artifacts_do_not_invent_deliveries()
    {
        var repo = Private(File("talks/agentops/content/talk.md", "# AgentOps\nSource to be added."));
        var catalog = CatalogBuilder.Build(repo, Public with { Files = [new("2026/Unknown/deck.pdf", "sha", null)] }, new(), new Dictionary<string, CatalogExtraction>(), DateTimeOffset.UtcNow);
        Assert.Contains("Practical agent operations", catalog.Talks[0].Abstract);
        Assert.Empty(catalog.Deliveries);
        Assert.True(Assert.Single(catalog.PublicArtifacts).NeedsReview);
    }

    [Fact]
    public void Explicit_previous_ids_deduplicate_but_lookalikes_are_preserved()
    {
        var catalog = Build(Private(
            File("engagements/2026/old/engagement.yaml", "id: old\nevent: Chippewa\ndate: 2026-01-01\n"),
            File("engagements/2026/new/engagement.yaml", "id: new\nevent: Chippewa Valley\ndate: 2026-03-14\nprevious_ids: [old]\n"),
            File("engagements/2026/cvcc/engagement.yaml", "id: cvcc\nevent: CVCC\ndate: 2026-01-01\n")));
        Assert.Equal(2, catalog.Deliveries.Count);
        Assert.Contains(catalog.Deliveries, d => d.DeliveryId == "cvcc");
        var review = new CatalogReviewRequest { ExpectedGenerationId = catalog.GenerationId, Delivery = new("cvcc", "new", null, null, null, null, null) };
        var merged = CatalogReview.Apply(catalog, review);
        Assert.Single(merged.Deliveries);
        var resynced = CatalogReview.ApplyOverrides(catalog with { DeliveryCorrections = merged.DeliveryCorrections });
        Assert.Single(resynced.Deliveries);
    }

    [Fact]
    public async Task Unchanged_heads_do_not_call_model_or_publish_again()
    {
        var repo = Private();
        var api = new FakeCatalogApi { Snapshot = Build(repo) };
        var chat = new FakeChatClient();
        var service = new CatalogSyncService(api, new FakeSource(repo), Options.Create(new CatalogOptions { Enabled = true, ExtractionModel = "test" }), NullLogger<CatalogSyncService>.Instance, chat);
        var result = await service.SyncAsync();
        Assert.Equal(api.Snapshot.GenerationId, result.GenerationId);
        Assert.Equal(0, api.PublishCount);
        Assert.Empty(chat.CapturedMessages);
    }

    [Theory]
    [InlineData(">-\n  Explicit folded description of practical\n  agent operations and evaluation for developers.")]
    [InlineData("|\n  Explicit literal description of practical\n  agent operations and evaluation for developers.")]
    [InlineData("'Author description'")]
    public async Task Parsed_author_descriptions_never_trigger_or_get_replaced_by_extraction(string description)
    {
        var text = "# Talk source\n" + new string('x', 350);
        var metadata = TalkYaml.Replace("Practical agent operations, observability and evaluation for developers.", description, StringComparison.Ordinal);
        var repo = new RepositorySnapshot("bhaydin/bhaydin-talks", "changed", [File("talks/agentops/talk.yaml", metadata), File("talks/agentops/content/talk.md", text)]);
        var api = new FakeCatalogApi();
        var chat = new FakeChatClient();
        var service = new CatalogSyncService(api, new FakeSource(repo), Options.Create(new CatalogOptions { Enabled = true, ExtractionModel = "test" }), NullLogger<CatalogSyncService>.Instance, chat);
        var first = await service.SyncAsync();
        Assert.Empty(chat.CapturedMessages);
        Assert.DoesNotContain("Extracted", first.Talks[0].Abstract);

        // A cached extraction from before the author supplied metadata must not overwrite it.
        var hash = CatalogBuilder.ContentHash(text);
        api.Extractions[hash] = new(hash, "Extracted text that must not overwrite author metadata.", "intermediate", ["invented"], "test");
        var updated = await service.SyncAsync();
        Assert.Equal(first.Talks[0].Abstract, updated.Talks[0].Abstract);
        Assert.Equal(new[] { "agents", "observability" }, updated.Talks[0].Topics);
        Assert.Empty(chat.CapturedMessages);
    }

    [Fact]
    public async Task Capped_extraction_resumes_on_unchanged_heads_then_becomes_a_true_noop()
    {
        var files = new List<RepositoryFile>();
        for (var i = 0; i < 7; i++)
        {
            files.Add(File($"talks/talk{i}/talk.yaml", $"id: talk{i}\ntitle: Talk {i}\nstatus: draft\n"));
            files.Add(File($"talks/talk{i}/content/talk.md", $"# Source {i}\n" + new string((char)('a' + i), 350)));
        }
        var repo = new RepositorySnapshot("bhaydin/bhaydin-talks", "same-head", files);
        var api = new FakeCatalogApi();
        var chat = new FakeChatClient();
        for (var i = 0; i < 7; i++) chat.Enqueue($$"""{"abstract":"Extracted concrete topic evidence for synthetic talk {{i}}.","audienceLevel":"advanced","topics":["operations"]}""");
        var service = new CatalogSyncService(api, new FakeSource(repo), Options.Create(new CatalogOptions { Enabled = true, ExtractionModel = "test", MaxExtractionCallsPerSync = 2 }), NullLogger<CatalogSyncService>.Instance, chat);
        foreach (var expected in new[] { 2, 4, 6, 7 })
        {
            var current = await service.SyncAsync();
            Assert.Equal(expected, chat.CapturedMessages.Count);
            Assert.Equal(expected, current.Talks.Count(t => !t.NeedsReview));
            Assert.Equal(expected, current.Talks.Count(t => t.AudienceLevel == "advanced" && t.Topics.Contains("operations")));
        }
        var generation = api.Snapshot.GenerationId;
        var publishes = api.PublishCount;
        var idle = await service.SyncAsync();
        Assert.Equal(generation, idle.GenerationId);
        Assert.Equal(publishes, api.PublishCount);
        Assert.Equal(7, chat.CapturedMessages.Count);
        Assert.Equal(7, api.Extractions.Count);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"abstract\":\"\",\"topics\":[]}")]
    public async Task Negative_extraction_results_are_cached_for_review_without_nightly_model_retries(string response)
    {
        var repo = new RepositorySnapshot("bhaydin/bhaydin-talks", "head", [
            File("talks/agentops/talk.yaml", "id: agentops\ntitle: AgentOps\n"), File("talks/agentops/content/talk.md", new string('x', 350))]);
        var api = new FakeCatalogApi();
        var chat = new FakeChatClient(); chat.Enqueue(response);
        var service = new CatalogSyncService(api, new FakeSource(repo), Options.Create(new CatalogOptions { Enabled = true, ExtractionModel = "test" }), NullLogger<CatalogSyncService>.Instance, chat);
        var first = await service.SyncAsync();
        var second = await service.SyncAsync();
        Assert.Single(chat.CapturedMessages);
        Assert.Single(api.Extractions);
        Assert.True(second.Talks[0].NeedsReview);
        Assert.Equal(first.GenerationId, second.GenerationId);
    }

    [Fact]
    public void Explicit_source_fields_win_over_cached_topics_level_and_older_extracted_versions()
    {
        var content = new string('x', 350);
        var hash = CatalogBuilder.ContentHash(content);
        var extractions = new Dictionary<string, CatalogExtraction> { [hash] = new(hash, "Extracted substantive summary with enough evidence to become a version.", "advanced", ["extracted-topic"], "test") };
        var files = new[] { File("talks/agentops/talk.yaml", "id: agentops\ntitle: AgentOps\n"), File("talks/agentops/content/talk.md", content) };
        var repo = new RepositorySnapshot("bhaydin/bhaydin-talks", "first", files);
        var first = CatalogBuilder.Build(repo, Public, new(), extractions, DateTimeOffset.UtcNow);
        Assert.Equal("advanced", first.Talks[0].AudienceLevel);
        var updated = CatalogBuilder.Build(repo with { CommitSha = "second", Files = [File(files[0].Path, TalkYaml + "\naudience_level: beginner\n"), files[1]] }, Public, first, extractions, DateTimeOffset.UtcNow);
        Assert.Equal("beginner", updated.Talks[0].AudienceLevel);
        Assert.Equal(new[] { "agents", "observability" }, updated.Talks[0].Topics);
        Assert.Contains("Practical agent operations", updated.Talks[0].Abstract);
        Assert.Contains(updated.Versions, v => v.Extracted);
        Assert.False(updated.Versions.Single(v => v.VersionId == updated.Talks[0].CurrentVersionId).Extracted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_claimed_jobs_record_health_and_failures_preserve_the_last_good_generation(bool fail)
    {
        var api = new FakeCatalogApi { Snapshot = Build(Private()) };
        var originalGeneration = api.Snapshot.GenerationId;
        var operations = new CatalogOperations();
        var reporter = new RunReporter(operations, new SilentNotifier(), TimeProvider.System);
        var service = new CatalogSyncService(api, new FakeSource(Private(), fail), Options.Create(new CatalogOptions { Enabled = true }),
            NullLogger<CatalogSyncService>.Instance, reporter: reporter);
        Assert.False(await service.ProcessNextAsync());
        Assert.Empty(await operations.ListAsync("runs", DateTimeOffset.MinValue));
        api.Job = new("job", "test", "lease", DateTimeOffset.UtcNow.AddMinutes(15));
        Assert.Equal(!fail, await service.ProcessNextAsync());
        var run = OperationState.Read<AgentRunRecord>(Assert.Single(await operations.ListAsync("runs", DateTimeOffset.MinValue)))!;
        Assert.Equal("catalog", run.Agent);
        Assert.Equal(fail ? "Failed" : "Succeeded", run.Status);
        Assert.NotNull(run.CompletedUtc);
        Assert.Equal(0, run.Metrics["extraction calls"]);
        Assert.Equal(originalGeneration, api.Snapshot.GenerationId);
        if (fail) Assert.Equal("SourceOrApiUnavailable", Assert.Single(run.Issues).Code);
    }

    [Theory]
    [InlineData("talk")]
    [InlineData("engagement")]
    [InlineData("duplicate")]
    public async Task Invalid_source_never_publishes_a_partial_generation(string malformed)
    {
        var delivery = File("engagements/2026/summit/engagement.yaml",
            "id: summit\ntalk_id: agentops\nevent: Summit\ndate: 2026-10-01\nstatus: completed\n");
        var original = Build(Private(delivery));
        var files = new List<RepositoryFile> { File("talks/agentops/talk.yaml", TalkYaml), delivery };
        if (malformed == "talk") files[0] = File(files[0].Path, "id: wrong\ntitle: Invalid");
        else if (malformed == "engagement") files[1] = File(delivery.Path, "id: summit\nevent: [broken");
        else files.Add(File("engagements/2026/duplicate/engagement.yaml", delivery.Text!));
        var api = new FakeCatalogApi { Snapshot = original, Job = new("job", "test", "lease", DateTimeOffset.UtcNow.AddMinutes(15)) };
        var operations = new CatalogOperations();
        var reporter = new RunReporter(operations, new SilentNotifier(), TimeProvider.System);
        var service = new CatalogSyncService(api, new FakeSource(new("bhaydin/bhaydin-talks", "changed", files)),
            Options.Create(new CatalogOptions { Enabled = true }), NullLogger<CatalogSyncService>.Instance, reporter: reporter);

        Assert.False(await service.ProcessNextAsync());
        Assert.Equal(0, api.PublishCount);
        Assert.Same(original, api.Snapshot);
        Assert.Single(api.Snapshot.Deliveries);
        var run = OperationState.Read<AgentRunRecord>(Assert.Single(await operations.ListAsync("runs", DateTimeOffset.MinValue)))!;
        Assert.Equal("Failed", run.Status);
        Assert.Equal("InvalidCatalogSource", Assert.Single(run.Issues).Code);
    }

    [Fact]
    public async Task Invalid_source_is_rejected_before_any_missing_abstract_model_call()
    {
        var repo = new RepositorySnapshot("bhaydin/bhaydin-talks", "bad", [
            File("talks/agentops/talk.yaml", "id: agentops\ntitle: AgentOps\n"),
            File("talks/agentops/content/talk.md", new string('x', 350)),
            File("engagements/2026/broken/engagement.yaml", "id: broken\nevent: [bad")]);
        var api = new FakeCatalogApi();
        var chat = new FakeChatClient();
        var service = new CatalogSyncService(api, new FakeSource(repo), Options.Create(new CatalogOptions { Enabled = true, ExtractionModel = "test" }), NullLogger<CatalogSyncService>.Instance, chat);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SyncAsync());
        Assert.Empty(chat.CapturedMessages);
        Assert.Empty(api.Extractions);
        Assert.Equal(0, api.PublishCount);
    }

    [Fact]
    public async Task Claimed_extraction_job_reports_calls_and_missing_provider_usage_explicitly()
    {
        var content = "# Draft\n" + string.Join(' ', Enumerable.Repeat("Concrete agent evaluation and tracing for production workloads.", 10));
        var repo = new RepositorySnapshot("bhaydin/bhaydin-talks", "changed", [
            File("talks/agentops/talk.yaml", TalkYaml.Replace("Practical agent operations, observability and evaluation for developers.", "", StringComparison.Ordinal)),
            File("talks/agentops/content/talk.md", content)]);
        var api = new FakeCatalogApi { Job = new("job", "test", "lease", DateTimeOffset.UtcNow.AddMinutes(15)) };
        var chat = new FakeChatClient(); chat.Enqueue("""{"abstract":"Concrete agent evaluation and tracing.","topics":["agents"],"audienceLevel":null}""");
        var operations = new CatalogOperations();
        var reporter = new RunReporter(operations, new SilentNotifier(), TimeProvider.System);
        var service = new CatalogSyncService(api, new FakeSource(repo), Options.Create(new CatalogOptions { Enabled = true, ExtractionModel = "synthetic" }),
            NullLogger<CatalogSyncService>.Instance, chat, reporter);
        Assert.True(await service.ProcessNextAsync());
        var run = OperationState.Read<AgentRunRecord>(Assert.Single(await operations.ListAsync("runs", DateTimeOffset.MinValue)))!;
        Assert.Equal(1, run.Changed);
        Assert.Equal(1, run.Metrics["extraction calls"]);
        Assert.Equal(1, run.Metrics["usage incomplete"]);
        Assert.Equal(1024, run.Metrics["budgeted completion tokens"]);
        Assert.Equal("synthetic", run.ModelDeployment);
        Assert.Null(run.EstimatedCost);
    }

    [Fact]
    public void Reviews_can_undo_aliases_clear_unknown_dates_and_cannot_create_alias_chains()
    {
        var catalog = Build(Private(
            File("engagements/2026/a/engagement.yaml", "id: a\nevent: A\ndate: 2026-01-01\n"),
            File("engagements/2026/b/engagement.yaml", "id: b\nevent: B\ndate: 2026-01-01\n"),
            File("engagements/2026/c/engagement.yaml", "id: c\nevent: C\ndate: 2026-01-01\n")));
        var merged = CatalogReview.Apply(catalog, new() { ExpectedGenerationId = catalog.GenerationId, Delivery = new("a", "b", null, null, null, null, null) });
        Assert.Equal(2, merged.Deliveries.Count);
        Assert.Equal(3, merged.SourceDeliveries.Count);
        Assert.Throws<ArgumentException>(() => CatalogReview.Apply(merged, new() { ExpectedGenerationId = merged.GenerationId, Delivery = new("b", "a", null, null, null, null, null) }));
        Assert.Throws<ArgumentException>(() => CatalogReview.Apply(merged, new() { ExpectedGenerationId = merged.GenerationId, Delivery = new("c", "a", null, null, null, null, null) }));
        var undone = CatalogReview.Apply(merged, new() { ExpectedGenerationId = merged.GenerationId, Delivery = new("a", null, null, "unknown", null, null, null) });
        Assert.Equal(3, undone.Deliveries.Count);
        Assert.Null(undone.Deliveries.Single(d => d.DeliveryId == "a").Date);
    }

    [Fact]
    public void Retired_talks_stay_ineligible_and_metadata_date_bumps_do_not_change_version_date()
    {
        var original = Build(Private());
        var changed = Build(new RepositorySnapshot("bhaydin/bhaydin-talks", "new", [File("talks/agentops/talk.yaml", TalkYaml.Replace("2026-08-01", "2026-10-15", StringComparison.Ordinal).Replace("status: active", "status: retired", StringComparison.Ordinal))]), original);
        Assert.Equal(original.Versions[0].EffectiveDate, Assert.Single(changed.Versions).EffectiveDate);
        var review = CatalogReview.Apply(changed, new() { ExpectedGenerationId = changed.GenerationId, TalkId = "agentops", Eligible = true });
        Assert.False(review.Talks[0].Eligible);
    }

    private sealed class FakeSource(RepositorySnapshot repository, bool fail = false) : ICatalogSource
    {
        public Task<RepositorySnapshot> ReadAsync(string name, CancellationToken ct = default) => fail ? throw new HttpRequestException("synthetic") : Task.FromResult(name.EndsWith("Presentations", StringComparison.Ordinal) ? Public : repository);
    }
    private sealed class FakeCatalogApi : ICatalogApiClient
    {
        public CatalogSnapshot Snapshot { get; set; } = new();
        public int PublishCount { get; private set; }
        public CatalogSyncJob? Job { get; set; }
        public Dictionary<string, CatalogExtraction> Extractions { get; } = [];
        public Task<CatalogSnapshot> GetCatalogAsync(CancellationToken ct = default) => Task.FromResult(Snapshot);
        public Task PublishCatalogAsync(CatalogPublishRequest request, CancellationToken ct = default) { PublishCount++; Snapshot = request.Snapshot; return Task.CompletedTask; }
        public Task<bool> EnqueueCatalogSyncAsync(CatalogSyncRequest request, CancellationToken ct = default) => Task.FromResult(true);
        public Task<CatalogSyncJob?> ClaimCatalogSyncAsync(CancellationToken ct = default) { var job = Job; Job = null; return Task.FromResult(job); }
        public Task CompleteCatalogSyncAsync(string id, CatalogJobCompletion request, CancellationToken ct = default) => Task.CompletedTask;
        public Task<CatalogExtraction?> GetCatalogExtractionAsync(string hash, CancellationToken ct = default) => Task.FromResult(Extractions.GetValueOrDefault(hash));
        public Task SaveCatalogExtractionAsync(CatalogExtraction extraction, CancellationToken ct = default) { Extractions[extraction.ContentHash] = extraction; return Task.CompletedTask; }
        public Task AcceptCatalogWebhookAsync(CatalogWebhookRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public Task<CatalogSnapshot> ReviewCatalogAsync(CatalogReviewRequest request, CancellationToken ct = default) => Task.FromResult(CatalogReview.Apply(Snapshot, request));
    }

    private sealed class SilentNotifier : INotifier
    {
        public Task NotifyAsync(Notification notification, CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class CatalogOperations : IOperationsApiClient
    {
        private readonly Dictionary<string, VersionedOperation> _items = [];
        private int _version;
        public Task<VersionedOperation?> GetAsync(string kind, string id, CancellationToken ct = default) => Task.FromResult(_items.GetValueOrDefault(kind + id));
        public Task<IReadOnlyList<VersionedOperation>> ListAsync(string kind, DateTimeOffset since, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<VersionedOperation>>(_items.Values.Where(v => v.Document.Kind == kind && v.Document.UpdatedUtc >= since).ToArray());
        public Task<VersionedOperation?> SaveAsync(OperationDocument document, string? expectedVersion = null, CancellationToken ct = default)
        {
            if (_items.GetValueOrDefault(document.Kind + document.Id)?.Version != expectedVersion) return Task.FromResult<VersionedOperation?>(null);
            var saved = new VersionedOperation(document, (++_version).ToString(System.Globalization.CultureInfo.InvariantCulture));
            _items[document.Kind + document.Id] = saved;
            return Task.FromResult<VersionedOperation?>(saved);
        }
    }
}
