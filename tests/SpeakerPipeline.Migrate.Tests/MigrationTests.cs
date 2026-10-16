using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SpeakerPipeline.Core;
using SpeakerPipeline.Migrate;

namespace SpeakerPipeline.Migrate.Tests;

public sealed class MigrationTests : IDisposable
{
    private readonly string _private = Path.Combine(Path.GetTempPath(), "speaker-pipeline-migrate-tests", Guid.NewGuid().ToString("n"));
    private static readonly Uri Local = new("http://localhost:5080/");

    [Theory]
    [InlineData("samples", "https://pipeline.example.org/", "--scope", "api://test/.default")]
    [InlineData("import", "https://pipeline.example.org/", "--source", "private")]
    [InlineData("samples", "http://localhost.evil.invalid/", "--scope", "api://test/.default")]
    [InlineData("samples", "http://localhost:5080/?token=secret", "--source", "samples")]
    public void Unsafe_or_implicit_destination_is_rejected(string command, string api, string key, string value)
        => Assert.Throws<ArgumentException>(() => CliOptions.Parse([command, "--api", api, key, value]));

    [Fact]
    public void Samples_and_imports_require_explicit_source_and_preview_by_default()
    {
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["samples", "--api", Local.AbsoluteUri]));
        var options = CliOptions.Parse(["samples", "--api", Local.AbsoluteUri, "--source", "samples"]);
        Assert.False(options.Apply);
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["samples", "--api", Local.AbsoluteUri, "--source", "samples", "--token", "dev"]));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("999")]
    [InlineData("0")]
    [InlineData(null)]
    public void Invalid_enum_is_never_silently_defaulted(string? category)
        => Assert.Throws<ArgumentException>(() => SeedMapping.ToRecord(new EventSeed { EventType = "Conference", Category = category, Priority = "High" }));

    [Fact]
    public async Task All_input_is_validated_before_any_network_or_write()
    {
        Directory.CreateDirectory(_private);
        File.WriteAllText(Path.Combine(_private, "sample-talks.json"), "[]");
        File.WriteAllText(Path.Combine(_private, "sample-events.json"), "[]");
        File.WriteAllText(Path.Combine(_private, "sample-submissions.json"), "[{\"unexpected\":\"never log me\"}]");
        var handler = new RecordingHandler();
        var error = new StringWriter();
        var code = await MigrationCli.RunAsync(["import", "--api", Local.AbsoluteUri, "--source", _private, "--apply"], handler, new StringWriter(), error);
        Assert.Equal(1, code);
        Assert.Empty(handler.Methods);
        Assert.DoesNotContain("never log me", error.ToString());
    }

    [Fact]
    public async Task Missing_reference_rejects_whole_import_before_writes()
    {
        WriteBatch(new ImportBatch([], [], [Submission("missing-event")]));
        var handler = new RecordingHandler();
        var code = await MigrationCli.RunAsync(["import", "--api", Local.AbsoluteUri, "--source", _private, "--apply"], handler, new StringWriter(), new StringWriter());
        Assert.Equal(1, code);
        Assert.All(handler.Methods, m => Assert.Equal(HttpMethod.Get, m));
    }

    [Fact]
    public async Task Import_preview_validates_but_never_writes()
    {
        WriteBatch(new ImportBatch([Talk()], [Event()], [Submission("real-event")]));
        var handler = new RecordingHandler();
        var code = await MigrationCli.RunAsync(["import", "--api", Local.AbsoluteUri, "--source", _private], handler, new StringWriter(), new StringWriter());
        Assert.Equal(0, code);
        Assert.Equal(2, handler.Methods.Count);
        Assert.All(handler.Methods, m => Assert.Equal(HttpMethod.Get, m));
    }

    [Fact]
    public void Duplicate_keys_and_invalid_mappings_abort_before_write()
    {
        var batch = new ImportBatch([Talk(), Talk()], [], []);
        Assert.Throws<ArgumentException>(() => batch.Validate(samples: false));
        Assert.Throws<ArgumentException>(() => SeedMapping.ToRecord(new EventSeed { EventType = "Conference", Category = "Monitor", Priority = "High", FocusFit = "AgentOps,Unrecognized" }));
    }

    [Fact]
    public void Manifest_only_deletes_exact_samples_and_retains_changed_or_unknown_properties()
    {
        var rows = KnownSamples();
        var first = rows[0];
        var changed = rows[1] with { Properties = new(rows[1].Properties) { ["Notes"] = new(MaintenanceValueType.String, "real edited notes") } };
        var unknown = rows[2] with { Properties = new(rows[2].Properties) { ["FutureColumn"] = new(MaintenanceValueType.String, "keep") } };
        var export = Export(new() { ["Events"] = [first, changed, unknown] });
        var manifest = ManifestBuilder.Build(export, Local);
        Assert.Single(manifest.Plan.Changes);
        Assert.Equal(first.RowKey, manifest.Plan.Changes[0].RowKey);
        Assert.Equal(2, manifest.Review.Count(r => r.Disposition == "retain-review"));
    }

    [Fact]
    public void Retained_submission_prevents_parent_sample_event_deletion()
    {
        var sample = KnownSamples()[0];
        var child = SampleCatalog.FromRecord(sample.RowKey, "real-submission", Submission(sample.RowKey), "EventSlug", "SubmissionId");
        var export = Export(new() { ["Events"] = [sample], ["Submissions"] = [child] });
        Assert.Empty(ManifestBuilder.Build(export, Local).Plan.Changes);
    }

    [Fact]
    public void All_four_talks_are_preserved_and_only_exact_placeholder_deck_is_removed()
    {
        var talks = SampleCatalog.TalkKeys.Select(key => new MaintenanceEntity
        {
            PartitionKey = "talks", RowKey = key, ETag = "etag",
            Properties = new() { ["DeckUrl"] = new(MaintenanceValueType.String, "<placeholder-sharepoint-deck-url>"), ["NewField"] = new(MaintenanceValueType.Int64, "99") },
        }).ToList();
        var manifest = ManifestBuilder.Build(Export(new() { ["Talks"] = talks }), Local);
        Assert.Equal(4, manifest.Plan.Changes.Count);
        Assert.All(manifest.Plan.Changes, c =>
        {
            Assert.Equal(MaintenanceChangeKind.RemoveProperties, c.Kind);
            Assert.Equal(new[] { "DeckUrl" }, c.PropertiesToRemove);
        });
        talks[0].Properties["DeckUrl"] = new(MaintenanceValueType.String, "https://real.invalid/deck");
        Assert.Equal(3, ManifestBuilder.Build(Export(new() { ["Talks"] = talks }), Local).Plan.Changes.Count);
    }

    [Fact]
    public void Snapshot_and_manifest_are_bound_to_target_content_and_operation()
    {
        var export = Export(new() { ["Events"] = [KnownSamples()[0]] });
        var manifest = ManifestBuilder.Build(export, Local);
        manifest.Validate(Local, export);
        Assert.Throws<ArgumentException>(() => manifest.Validate(new Uri("http://localhost:6000/"), export));
        Assert.Throws<ArgumentException>(() => (manifest with { Plan = manifest.Plan with { OperationId = "tampered" } }).Validate(Local, export));
        Assert.Throws<ArgumentException>(() => ManifestBuilder.Build(export with { Snapshot = export.Snapshot with { WritersFrozen = false } }, Local));
    }

    [Fact]
    public async Task Cleanup_preview_is_offline_and_preserves_source()
    {
        var snapshot = Path.Combine(_private, "snapshot.json");
        var manifest = Path.Combine(_private, "manifest.json");
        var export = Export(new() { ["Events"] = [KnownSamples()[0]] });
        PrivateFiles.WriteNew(snapshot, export);
        var original = File.ReadAllBytes(snapshot);
        var handler = new RecordingHandler();
        Assert.Equal(0, await MigrationCli.RunAsync(["preview", "--api", Local.AbsoluteUri, "--source", snapshot, "--output", manifest], handler, new StringWriter(), new StringWriter()));
        Assert.Empty(handler.Methods);
        Assert.Equal(original, File.ReadAllBytes(snapshot));
        Assert.Single(PrivateFiles.Read<CleanupManifest>(manifest).Plan.Changes);
    }

    [Fact]
    public async Task Recompute_cli_writes_private_proposals_without_api_requests()
    {
        var rows = KnownSamples();
        var beforePath = Path.Combine(_private, "before.json");
        var afterPath = Path.Combine(_private, "after.json");
        var reportPath = Path.Combine(_private, "recompute.json");
        var repairsPath = Path.Combine(_private, "repairs.json");
        PrivateFiles.WriteNew(beforePath, Export(new() { ["Events"] = rows }));
        PrivateFiles.WriteNew(afterPath, Export(new() { ["Events"] = [] }));
        var handler = new RecordingHandler();
        Assert.Equal(0, await MigrationCli.RunAsync(["recompute", "--api", Local.AbsoluteUri, "--source", beforePath, "--snapshot", afterPath,
            "--at", "2026-10-16T00:00:00Z", "--prep-window-days", "28", "--prep-threshold", "2", "--output", reportPath, "--repairs-output", repairsPath],
            handler, new StringWriter(), new StringWriter()));
        Assert.Empty(handler.Methods);
        Assert.Empty(PrivateFiles.Read<RecomputeReport>(reportPath).ScoringChanges);
        Assert.Empty(PrivateFiles.Read<CleanupManifest>(repairsPath).Plan.Changes);
    }

    [Fact]
    public async Task Mismatched_manifest_prevents_api_request()
    {
        var export = Export(new());
        var manifest = ManifestBuilder.Build(export, Local);
        PrivateFiles.WriteNew(Path.Combine(_private, "snapshot.json"), export);
        PrivateFiles.WriteNew(Path.Combine(_private, "manifest.json"), manifest with { Checksum = "bad" });
        var handler = new RecordingHandler();
        Assert.Equal(1, await MigrationCli.RunAsync(["apply", "--api", Local.AbsoluteUri, "--source", Path.Combine(_private, "manifest.json"), "--snapshot", Path.Combine(_private, "snapshot.json"), "--output", Path.Combine(_private, "result.json")], handler, new StringWriter(), new StringWriter()));
        Assert.Empty(handler.Methods);
    }

    [Fact]
    public void Private_files_reject_git_worktrees_and_overwriting()
    {
        var repository = Path.Combine(_private, "repository");
        Directory.CreateDirectory(repository);
        File.WriteAllText(Path.Combine(repository, ".git"), "gitdir: somewhere");
        Assert.Throws<ArgumentException>(() => PrivateFiles.ValidatePath(Path.Combine(repository, "snapshot.json")));
        var target = Path.Combine(_private, "private.json");
        PrivateFiles.WriteNew(target, new { Count = 1 });
        Assert.Throws<IOException>(() => PrivateFiles.WriteNew(target, new { Count = 2 }));
    }

    [Fact]
    public async Task Apply_preflights_then_reuses_same_operation_and_snapshot_on_retry()
    {
        var export = Export(new() { ["Events"] = [KnownSamples()[0]] });
        var manifest = ManifestBuilder.Build(export, Local);
        PrivateFiles.WriteNew(Path.Combine(_private, "snapshot.json"), export);
        PrivateFiles.WriteNew(Path.Combine(_private, "manifest.json"), manifest);
        var requests = new List<MaintenanceApplyRequest>();
        var calls = 0;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var handler = new CallbackHandler(async request =>
            {
                Assert.Equal("/v1/admin/data/cleanup", request.RequestUri!.AbsolutePath);
                var body = (await request.Content!.ReadFromJsonAsync<MaintenanceApplyRequest>(PrivateFiles.Json))!;
                requests.Add(body);
                calls++;
                if (calls == 2) return new(HttpStatusCode.ServiceUnavailable);
                return JsonResponse(new MaintenanceResult
                {
                    OperationId = body.Plan.OperationId, SnapshotChecksum = body.Plan.Snapshot.Checksum,
                    Status = body.Apply ? "Completed" : "Preview", ChangeCount = 1, CompletedCount = body.Apply ? 1 : 0, Applied = body.Apply,
                });
            });
            var resultPath = Path.Combine(_private, $"result-{attempt}.json");
            var result = await MigrationCli.RunAsync(["apply", "--api", Local.AbsoluteUri, "--source", Path.Combine(_private, "manifest.json"), "--snapshot", Path.Combine(_private, "snapshot.json"), "--output", resultPath], handler, new StringWriter(), new StringWriter());
            Assert.Equal(attempt == 0 ? 2 : 0, result);
            Assert.True(File.Exists(resultPath));
        }
        Assert.Equal(new[] { false, true, false, true }, requests.Select(r => r.Apply));
        Assert.All(requests, r => Assert.Equal(manifest.Plan.OperationId, r.Plan.OperationId));
        Assert.All(requests, r => Assert.Equal(export.Snapshot.Checksum, r.Plan.Snapshot.Checksum));
    }

    [Fact]
    public async Task Restore_checks_journal_binding_and_previews_by_default()
    {
        var export = Export(new());
        var manifest = ManifestBuilder.Build(export, Local);
        PrivateFiles.WriteNew(Path.Combine(_private, "snapshot.json"), export);
        PrivateFiles.WriteNew(Path.Combine(_private, "manifest.json"), manifest);
        var result = new MaintenanceResult { OperationId = manifest.Plan.OperationId, SnapshotChecksum = export.Snapshot.Checksum, Status = "Preview" };
        var posts = 0;
        var handler = new CallbackHandler(async request =>
        {
            if (request.Method == HttpMethod.Get)
                Assert.Equal($"/v1/admin/data/operations/{manifest.Plan.OperationId}", request.RequestUri!.AbsolutePath);
            else
            {
                var body = (await request.Content!.ReadFromJsonAsync<MaintenanceRestoreRequest>(PrivateFiles.Json))!;
                Assert.False(body.Apply);
                Assert.Equal(manifest.Plan.OperationId, body.OperationId);
                posts++;
            }
            return JsonResponse(result);
        });
        Assert.Equal(0, await MigrationCli.RunAsync(["restore", "--api", Local.AbsoluteUri, "--source", Path.Combine(_private, "manifest.json"), "--snapshot", Path.Combine(_private, "snapshot.json"), "--output", Path.Combine(_private, "restore.json")], handler, new StringWriter(), new StringWriter()));
        Assert.Equal(1, posts);
    }

    [Fact]
    public async Task Local_admin_auth_is_distinct_and_cannot_be_sent_remotely()
    {
        var reached = 0;
        var transport = new CallbackHandler(request =>
        {
            Assert.Equal("maintenance", request.Headers.Authorization!.Parameter);
            reached++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var client = new HttpClient(new OperatorTokenHandler(null, null, maintenance: true) { InnerHandler = transport });
        using var response = await client.GetAsync(Local);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("https://remote.invalid/"));
        Assert.Equal(1, reached);
    }

    [Fact]
    public async Task Full_snapshot_restore_allows_only_original_origin_or_local_rehearsal_and_previews_by_default()
    {
        var source = Export(new()) with { ApiOrigin = "https://production.invalid/" };
        PrivateFiles.WriteNew(Path.Combine(_private, "snapshot.json"), source);
        var calls = 0;
        var handler = new CallbackHandler(async request =>
        {
            Assert.Equal("/v1/admin/data/restore-snapshot", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.False(body.RootElement.GetProperty("apply").GetBoolean());
            Assert.Equal(source.Snapshot.Checksum, body.RootElement.GetProperty("snapshot").GetProperty("checksum").GetString());
            calls++;
            return JsonResponse(new MaintenanceResult { OperationId = "restore-preview", SnapshotChecksum = source.Snapshot.Checksum, Status = "Preview" });
        });
        Assert.Equal(0, await MigrationCli.RunAsync(["restore-snapshot", "--api", Local.AbsoluteUri, "--source", Path.Combine(_private, "snapshot.json"), "--output", Path.Combine(_private, "rehearsal.json")], handler, new StringWriter(), new StringWriter()));
        Assert.Equal(1, calls);
        var blockedHandler = new RecordingHandler();
        Assert.Equal(1, await MigrationCli.RunAsync(["restore-snapshot", "--api", "https://other.invalid/", "--scope", "api://test/.default", "--source", Path.Combine(_private, "snapshot.json"), "--output", Path.Combine(_private, "blocked.json"), "--apply"], blockedHandler, new StringWriter(), new StringWriter()));
        Assert.Empty(blockedHandler.Methods);
    }

    private static SnapshotFile Export(Dictionary<string, List<MaintenanceEntity>> tables)
    {
        foreach (var name in MaintenanceIntegrity.TableNames) tables.TryAdd(name, []);
        var snapshot = new MaintenanceSnapshot
        {
            SnapshotId = "test-snapshot", CreatedUtc = DateTimeOffset.Parse("2026-10-16T00:00:00Z"), WritersFrozen = true,
            Target = new() { TableEndpoint = "https://test.table.core.windows.net", TableNames = MaintenanceIntegrity.TableNames.ToDictionary(n => n, n => n) }, Tables = tables,
        };
        return new(Local.AbsoluteUri, snapshot with { Checksum = MaintenanceIntegrity.SnapshotChecksum(snapshot) });
    }

    private static List<MaintenanceEntity> KnownSamples()
    {
        using var stream = typeof(SampleCatalog).Assembly.GetManifestResourceStream("Samples.events.json")!;
        var samples = JsonSerializer.Deserialize<List<EventSeed>>(stream, PrivateFiles.Json)!;
        return samples.Select(SeedMapping.ToRecord).Select(r => SampleCatalog.FromRecord("events", r.Slug, r, "Slug")).ToList();
    }

    private void WriteBatch(ImportBatch batch)
    {
        Directory.CreateDirectory(_private);
        File.WriteAllText(Path.Combine(_private, "sample-talks.json"), JsonSerializer.Serialize(batch.Talks.Select(t => new { PartitionKey = "talks", RowKey = t.Slug, t.CanonicalTitle, Lane = t.Lane.ToString() })));
        File.WriteAllText(Path.Combine(_private, "sample-events.json"), JsonSerializer.Serialize(batch.Events.Select(e => new { PartitionKey = "events", RowKey = e.Slug, e.Name, EventType = e.EventType.ToString(), Category = e.Category.ToString(), Priority = e.Priority.ToString() })));
        File.WriteAllText(Path.Combine(_private, "sample-submissions.json"), JsonSerializer.Serialize(batch.Submissions.Select(s => new { PartitionKey = s.EventSlug, RowKey = s.SubmissionId, s.EventName, s.TalkSlug, s.TalkTitleUsed, s.AbstractUsed, s.SubmittedOnUtc, Status = s.Status.ToString() })));
    }

    private static EventRecord Event() => new() { Slug = "real-event", Name = "Real", EventType = EventType.Conference, Category = EventCategory.Monitor, Priority = Priority.High };
    private static TalkRecord Talk() => new() { Slug = "real-talk", CanonicalTitle = "Real", Lane = Lane.AgentOps };
    private static SubmissionRecord Submission(string eventSlug) => new() { EventSlug = eventSlug, SubmissionId = "submission-1", EventName = "Real", TalkSlug = "real-talk", TalkTitleUsed = "Real", AbstractUsed = "Abstract", SubmittedOnUtc = DateTimeOffset.UtcNow, Status = SubmissionStatus.Submitted };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpMethod> Methods { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<object>()) });
        }
    }

    private sealed class CallbackHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request);
    }

    private static HttpResponseMessage JsonResponse<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: PrivateFiles.Json) };

    public void Dispose()
    {
        // Tests own this fixed temp child only; never delete an input or working tree.
        var resolved = Path.GetFullPath(_private);
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "speaker-pipeline-migrate-tests")) + Path.DirectorySeparatorChar;
        if (resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
}
