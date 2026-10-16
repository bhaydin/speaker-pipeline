using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using SpeakerPipeline.Core;
using SpeakerPipeline.Storage;

namespace SpeakerPipeline.Storage.Tests;

public sealed class MaintenanceTests
{
    [Fact]
    public void Raw_mapping_preserves_all_table_types_and_unknown_properties()
    {
        var source = new TableEntity("p", "r")
        {
            ETag = new ETag("exact-tag"), Timestamp = DateTimeOffset.Parse("2026-10-16T12:00:00Z"),
            ["FutureString"] = "unknown schema content", ["Flag"] = true, ["Int"] = int.MaxValue,
            ["Long"] = long.MaxValue, ["Double"] = 0.12345678912345678d,
            ["Date"] = DateTimeOffset.Parse("2026-10-16T12:13:14.1234567Z"),
            ["Guid"] = Guid.NewGuid(), ["Binary"] = new byte[] { 0, 1, 255 },
        };
        var snapshot = MaintenanceRawMapping.FromEntity(source);
        var restored = MaintenanceRawMapping.ToEntity(snapshot);
        Assert.Equal(source.ETag.ToString(), snapshot.ETag);
        Assert.Equal(source.Timestamp, snapshot.Timestamp);
        Assert.Equal(8, snapshot.Properties.Count);
        foreach (var name in snapshot.Properties.Keys) Assert.Equal(source[name], restored[name]);
        Assert.Equal(default, restored.ETag); // Azure assigns fresh system metadata on restore.
        Assert.Null(restored.Timestamp);
    }

    [Fact]
    public async Task Export_covers_every_table_and_orphan_partitions_without_domain_projection()
    {
        var store = new MemoryStore();
        store.Put("Submissions", Row("missing-event", "orphan", ("TalkSlug", "missing-talk")));
        store.Put("NotificationLog", Row("2020-02", "old", ("Unknown", "kept")));
        var snapshot = await new PipelineMaintenanceService(store).ExportAsync(false);
        Assert.Equal(6, snapshot.Tables.Count);
        Assert.False(snapshot.WritersFrozen);
        Assert.Contains(snapshot.Tables["Submissions"], e => e.PartitionKey == "missing-event");
        Assert.Single(snapshot.Tables["NotificationLog"]);
        Assert.Equal(MaintenanceIntegrity.SnapshotChecksum(snapshot), snapshot.Checksum);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task Preview_is_read_only_and_checksums_ignore_dictionary_insertion_order()
    {
        var (store, service, plan) = await Setup();
        var reversed = plan.Snapshot with { Tables = plan.Snapshot.Tables.Reverse().ToDictionary(p => p.Key, p => p.Value) };
        Assert.Equal(plan.Snapshot.Checksum, MaintenanceIntegrity.SnapshotChecksum(reversed));
        var result = await service.CleanupAsync(new() { Plan = plan });
        Assert.Equal("Preview", result.Status);
        Assert.Equal(0, store.Writes);
        Assert.Null(store.Journal);
    }

    [Fact]
    public async Task Apply_deletes_children_first_repairs_without_losing_unknowns_and_is_idempotent()
    {
        var (store, service, plan) = await Setup();
        var result = await service.CleanupAsync(new() { Plan = plan, Apply = true });
        Assert.Equal("Completed", result.Status);
        Assert.Equal(3, result.CompletedCount);
        Assert.Equal(new[] { "Submissions/demo/s", "Events/events/demo", "Talks/talks/canonical" }, store.Mutations);
        var talk = store.Rows[("Talks", "talks", "canonical")];
        Assert.False(talk.Properties.ContainsKey("DeckUrl"));
        Assert.Equal("preserve", talk.Properties["FutureSchema"].Value);
        var writes = store.Writes;
        await service.CleanupAsync(new() { Plan = plan, Apply = true });
        Assert.Equal(writes, store.Writes);
    }

    [Fact]
    public async Task Apply_rejects_unfrozen_snapshot_wrong_target_checksum_or_stale_rows_before_writing()
    {
        var (store, service, plan) = await Setup();
        var unfrozen = plan.Snapshot with { WritersFrozen = false };
        unfrozen = unfrozen with { Checksum = MaintenanceIntegrity.SnapshotChecksum(unfrozen) };
        await Assert.ThrowsAsync<ArgumentException>(() => service.CleanupAsync(new() { Plan = plan with { Snapshot = unfrozen }, Apply = true }));
        var wrongTarget = plan.Snapshot with { Target = plan.Snapshot.Target with { TableEndpoint = "https://other.table.core.windows.net" } };
        wrongTarget = wrongTarget with { Checksum = MaintenanceIntegrity.SnapshotChecksum(wrongTarget) };
        await Assert.ThrowsAsync<ArgumentException>(() => service.CleanupAsync(new() { Plan = plan with { Snapshot = wrongTarget }, Apply = true }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CleanupAsync(new() { Plan = plan with { Snapshot = plan.Snapshot with { Checksum = "tampered" } }, Apply = true }));
        store.Put("Events", store.Rows[("Events", "events", "real")] with { ETag = "changed" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CleanupAsync(new() { Plan = plan, Apply = true }));
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task Reference_guard_rejects_deleting_a_parent_of_a_retained_submission()
    {
        var (store, service, plan) = await Setup();
        var parentOnly = plan with { Changes = [plan.Changes.Single(c => c.Table == "Events")] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CleanupAsync(new() { Plan = parentOnly, Apply = true }));
        Assert.Equal(0, store.Writes);
    }

    [Theory]
    [InlineData("Submissions")]
    [InlineData("Talks")]
    public async Task Resume_recovers_a_mutation_completed_before_its_checkpoint(string crashTable)
    {
        var (store, service, plan) = await Setup();
        store.CrashAfterMutationTable = crashTable;
        await Assert.ThrowsAsync<IOException>(() => service.CleanupAsync(new() { Plan = plan, Apply = true }));
        Assert.Contains(store.Journal!.Items, i => i.State == "Executing");
        var resumed = await service.CleanupAsync(new() { Plan = plan, Apply = true });
        Assert.Equal("Completed", resumed.Status);
        Assert.Equal(3, store.Mutations.Count);
    }

    [Fact]
    public async Task Restore_is_guarded_scoped_and_preserves_new_unrelated_data()
    {
        var (store, service, plan) = await Setup();
        await service.CleanupAsync(new() { Plan = plan, Apply = true });
        store.Put("Topics", Row("topic", "new-real-idea", ("Title", "Keep me")));
        var previewWrites = store.Writes;
        await service.RestoreAsync(new() { OperationId = plan.OperationId });
        Assert.Equal(previewWrites, store.Writes);
        var result = await service.RestoreAsync(new() { OperationId = plan.OperationId, Apply = true });
        Assert.True(result.Restored);
        foreach (var table in plan.Snapshot.Tables)
            foreach (var before in table.Value)
                Assert.Equal(MaintenanceIntegrity.EntityChecksum(before), MaintenanceIntegrity.EntityChecksum(store.Rows[(table.Key, before.PartitionKey, before.RowKey)]));
        Assert.True(store.Rows.ContainsKey(("Topics", "topic", "new-real-idea")));
        var writes = store.Writes;
        await service.RestoreAsync(new() { OperationId = plan.OperationId, Apply = true });
        Assert.Equal(writes, store.Writes);
    }

    [Fact]
    public async Task Restore_refuses_overwriting_changes_since_cleanup()
    {
        var (store, service, plan) = await Setup();
        await service.CleanupAsync(new() { Plan = plan, Apply = true });
        var talk = store.Rows[("Talks", "talks", "canonical")];
        store.Put("Talks", talk with { ETag = "someone-edited-this" });
        var writes = store.Writes;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(new() { OperationId = plan.OperationId, Apply = true }));
        Assert.Equal(writes, store.Writes);
    }

    [Fact]
    public async Task Restore_resumes_after_insert_before_checkpoint_and_cleanup_cannot_restart()
    {
        var (store, service, plan) = await Setup();
        await service.CleanupAsync(new() { Plan = plan, Apply = true });
        store.CrashAfterMutationTable = "Events";
        await Assert.ThrowsAsync<IOException>(() => service.RestoreAsync(new() { OperationId = plan.OperationId, Apply = true }));
        var result = await service.RestoreAsync(new() { OperationId = plan.OperationId, Apply = true });
        Assert.True(result.Restored);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CleanupAsync(new() { Plan = plan, Apply = true }));
    }

    [Fact]
    public async Task Operation_id_cannot_be_reused_for_a_different_plan()
    {
        var (_, service, plan) = await Setup();
        await service.CleanupAsync(new() { Plan = plan, Apply = true });
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CleanupAsync(new() { Plan = plan with { Purpose = "a different operation" }, Apply = true }));
    }

    [Fact]
    public async Task Full_snapshot_restore_is_create_only_lossless_and_retries_partial_inserts()
    {
        var (source, sourceService, _) = await Setup();
        source.Put("Submissions", Row("orphan-event", "orphan", ("UnknownFutureProperty", "preserve even orphan rows")));
        source.Put("Blackouts", Row("blackout", "family", ("Reason", "personal")));
        source.Put("NotificationLog", Row("2020-02", "log", ("DedupeKey", "do-not-repeat")));
        var snapshot = await sourceService.ExportAsync(true);
        var destination = new MemoryStore { ThrowAfterNextAdd = true };
        var restore = new PipelineMaintenanceService(destination);
        var preview = await restore.RestoreSnapshotAsync(new() { Snapshot = snapshot });
        Assert.Equal(0, preview.CompletedCount);
        Assert.Equal(0, destination.Writes);
        await Assert.ThrowsAsync<IOException>(() => restore.RestoreSnapshotAsync(new() { Snapshot = snapshot, Apply = true }));
        Assert.Single(destination.Rows);
        var result = await restore.RestoreSnapshotAsync(new() { Snapshot = snapshot, Apply = true });
        Assert.True(result.Restored);
        Assert.Equal(source.Rows.Count, result.CompletedCount);
        foreach (var (key, original) in source.Rows)
            Assert.Equal(MaintenanceIntegrity.EntityChecksum(original), MaintenanceIntegrity.EntityChecksum(destination.Rows[key]));
        var writes = destination.Writes;
        await restore.RestoreSnapshotAsync(new() { Snapshot = snapshot, Apply = true });
        Assert.Equal(writes, destination.Writes);
    }

    [Fact]
    public async Task Full_restore_refuses_extra_or_conflicting_rows_before_any_write()
    {
        var (_, _, plan) = await Setup();
        var destination = new MemoryStore();
        destination.Put("Topics", Row("topic", "extra", ("Title", "must survive")));
        var restore = new PipelineMaintenanceService(destination);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restore.RestoreSnapshotAsync(new() { Snapshot = plan.Snapshot, Apply = true }));
        Assert.Equal(0, destination.Writes);
        destination.Rows.Clear();
        destination.Put("Events", Row("events", "demo", ("Name", "real edited row")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restore.RestoreSnapshotAsync(new() { Snapshot = plan.Snapshot, Apply = true }));
        Assert.Equal(0, destination.Writes);
    }

    [Fact]
    public async Task Cross_target_restore_requires_explicit_server_local_mode_and_actual_loopback()
    {
        var (_, _, plan) = await Setup();
        var local = new MemoryStore("http://127.0.0.1:11002/devstoreaccount1");
        var restore = new PipelineMaintenanceService(local);
        await Assert.ThrowsAsync<ArgumentException>(() => restore.RestoreSnapshotAsync(new() { Snapshot = plan.Snapshot, Apply = true }));
        var result = await restore.RestoreSnapshotAsync(new() { Snapshot = plan.Snapshot, Apply = true }, allowLocalRehearsal: true);
        Assert.True(result.Restored);
        var otherCloud = new MemoryStore("https://another.table.core.windows.net");
        await Assert.ThrowsAsync<ArgumentException>(() => new PipelineMaintenanceService(otherCloud).RestoreSnapshotAsync(
            new() { Snapshot = plan.Snapshot, Apply = true }, allowLocalRehearsal: true));
        Assert.Equal(0, otherCloud.Writes);
    }

    [Fact]
    public async Task Frozen_export_refuses_rows_changed_by_an_undrained_writer()
    {
        var (store, service, _) = await Setup();
        var verificationRead = store.Reads + 2;
        store.BeforeRead = count =>
        {
            if (count == verificationRead)
                store.Put("Events", store.Rows[("Events", "events", "real")] with { ETag = "concurrent-writer" });
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportAsync(true));
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task Derived_flag_repair_preserves_content_and_unknowns_and_rolls_back()
    {
        var (store, service, originalPlan) = await Setup();
        var before = originalPlan.Snapshot.Tables["Events"].Single(e => e.RowKey == "real");
        var change = new MaintenanceChange
        {
            Table = "Events", PartitionKey = before.PartitionKey, RowKey = before.RowKey, ExpectedETag = before.ETag,
            Kind = MaintenanceChangeKind.SetProperties,
            PropertiesToSet = new() { ["FamilyConflictFlag"] = new(MaintenanceValueType.Boolean, "true") },
        };
        var plan = originalPlan with { Changes = [change] };
        var hash = MaintenanceIntegrity.PlanChecksum(plan);
        Assert.NotEqual(hash, MaintenanceIntegrity.PlanChecksum(plan with { Changes = [change with { PropertiesToSet = new() { ["FamilyConflictFlag"] = new(MaintenanceValueType.Boolean, "false") } }] }));
        await service.CleanupAsync(new() { Plan = plan, Apply = true });
        var repaired = store.Rows[("Events", "events", "real")];
        Assert.Equal(before.Properties["Name"], repaired.Properties["Name"]);
        Assert.Equal(new(MaintenanceValueType.Boolean, "true"), repaired.Properties["FamilyConflictFlag"]);
        await service.RestoreAsync(new() { OperationId = plan.OperationId, Apply = true });
        Assert.Equal(MaintenanceIntegrity.EntityChecksum(before), MaintenanceIntegrity.EntityChecksum(store.Rows[("Events", "events", "real")]));
    }

    [Theory]
    [InlineData("Category", MaintenanceValueType.String, "Monitor")]
    [InlineData("DoNotResurface", MaintenanceValueType.Boolean, "false")]
    [InlineData("Name", MaintenanceValueType.String, "Changed content")]
    [InlineData("FamilyConflictFlag", MaintenanceValueType.String, "true")]
    public async Task Derived_repair_rejects_decisions_content_and_wrong_types(string property, MaintenanceValueType type, string value)
    {
        var (store, service, originalPlan) = await Setup();
        var before = originalPlan.Snapshot.Tables["Events"].Single(e => e.RowKey == "real");
        var change = new MaintenanceChange
        {
            Table = "Events", PartitionKey = before.PartitionKey, RowKey = before.RowKey, ExpectedETag = before.ETag,
            Kind = MaintenanceChangeKind.SetProperties, PropertiesToSet = new() { [property] = new(type, value) },
        };
        await Assert.ThrowsAsync<ArgumentException>(() => service.CleanupAsync(new() { Plan = originalPlan with { Changes = [change] }, Apply = true }));
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task Losing_the_maintenance_lease_stops_business_mutations()
    {
        var (store, service, plan) = await Setup();
        store.LoseLockAfterCheckpoint = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CleanupAsync(new() { Plan = plan, Apply = true }));
        Assert.Empty(store.Mutations);
        Assert.NotNull(store.Journal); // Original snapshot is already safe; no business mutation ran.
        Assert.All(store.Journal.Items, item => Assert.Equal("Planned", item.State));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reviewed_scoring_repair_preserves_manual_content_resumes_and_rolls_back(bool interrupt)
    {
        var (store, service, plan) = await ScoringSetup();
        var original = plan.Snapshot.Tables["Events"].Single(e => e.RowKey == "real");
        await service.CleanupAsync(new() { Plan = plan });
        Assert.Equal(0, store.Writes);
        if (interrupt)
        {
            store.CrashAfterMutationTable = "Events";
            await Assert.ThrowsAsync<IOException>(() => service.CleanupAsync(new() { Plan = plan, Apply = true }));
            Assert.Equal("Executing", Assert.Single(store.Journal!.Items).State);
        }
        await service.CleanupAsync(new() { Plan = plan, Apply = true });
        var repaired = store.Rows[("Events", "events", "real")];
        foreach (var property in original.Properties.Where(p => !plan.Changes[0].PropertiesToSet.ContainsKey(p.Key)))
            Assert.Equal(property.Value, repaired.Properties[property.Key]);
        Assert.Equal(original.Properties.Count, repaired.Properties.Count);
        Assert.Equal("Refreshed rationale after removing fictional commitments.", repaired.Properties["NextAction"].Value);
        Assert.Single(store.Mutations); // A crash after replacement does not cause a second replacement.
        var writes = store.Writes;
        await service.CleanupAsync(new() { Plan = plan, Apply = true });
        Assert.Equal(writes, store.Writes);
        await service.RestoreAsync(new() { OperationId = plan.OperationId, Apply = true });
        Assert.Equal(MaintenanceIntegrity.EntityChecksum(original), MaintenanceIntegrity.EntityChecksum(store.Rows[("Events", "events", "real")]));
    }

    [Theory]
    [InlineData("Accepted")]
    [InlineData("Submitted")]
    [InlineData("Delivered")]
    [InlineData("Quarantine")]
    [InlineData("Skip")]
    [InlineData("Pass")]
    [InlineData("manual-unmarked")]
    [InlineData("blank-agent")]
    [InlineData("wrong-agent-type")]
    [InlineData("manual-agent")]
    [InlineData("empty-agent-version")]
    [InlineData("suppressed")]
    [InlineData("wrong-suppression-type")]
    [InlineData("submission")]
    public async Task Reviewed_scoring_repair_rejects_protected_or_unmarked_originals(string guard)
    {
        var (store, service, plan) = await ScoringSetup();
        var original = store.Rows[("Events", "events", "real")];
        var properties = new Dictionary<string, MaintenanceValue>(original.Properties);
        switch (guard)
        {
            case "manual-unmarked": properties.Remove("DecidedByAgent"); break;
            case "blank-agent": properties["DecidedByAgent"] = new(MaintenanceValueType.String, " "); break;
            case "wrong-agent-type": properties["DecidedByAgent"] = new(MaintenanceValueType.Int32, "1"); break;
            case "manual-agent": properties["DecidedByAgent"] = new(MaintenanceValueType.String, "manual"); break;
            case "empty-agent-version": properties["DecidedByAgent"] = new(MaintenanceValueType.String, "scoring-agent-"); break;
            case "suppressed": properties["DoNotResurface"] = new(MaintenanceValueType.Boolean, "true"); break;
            case "wrong-suppression-type": properties["DoNotResurface"] = new(MaintenanceValueType.String, "false"); break;
            case "submission": store.Put("Submissions", Row("real", "kept", ("TalkSlug", "canonical"))); break;
            default: properties["Category"] = new(MaintenanceValueType.String, guard); break;
        }
        store.Put("Events", original with { Properties = properties });
        plan = plan with { Snapshot = await service.ExportAsync(true) };
        await Assert.ThrowsAsync<ArgumentException>(() => service.CleanupAsync(new() { Plan = plan, Apply = true }));
        Assert.Equal(0, store.Writes);
    }

    [Theory]
    [InlineData("Category", "Skip")]
    [InlineData("Category", "Accepted")]
    [InlineData("Category", "Outreach")]
    [InlineData("Priority", "Committed")]
    [InlineData("Priority", "High")]
    [InlineData("DecidedByAgent", " ")]
    [InlineData("DecidedByAgent", "manual")]
    [InlineData("DecidedByAgent", "scoring-agent-")]
    [InlineData("NextAction", " ")]
    [InlineData("extra", "Notes")]
    [InlineData("extra", "DoNotResurface")]
    [InlineData("extra", "FamilyConflictFlag")]
    [InlineData("missing", "Priority")]
    [InlineData("wrong-type", "NextAction")]
    [InlineData("remove", "Notes")]
    [InlineData("too-long", "NextAction")]
    public async Task Reviewed_scoring_repair_rejects_unapproved_fields_types_and_pairs(string field, string value)
    {
        var (store, service, plan) = await ScoringSetup();
        var change = plan.Changes[0];
        var properties = new Dictionary<string, MaintenanceValue>(change.PropertiesToSet);
        switch (field)
        {
            case "extra": properties[value] = new(MaintenanceValueType.String, "unauthorized"); break;
            case "missing": properties.Remove(value); break;
            case "wrong-type": properties[value] = new(MaintenanceValueType.Boolean, "true"); break;
            case "remove": change = change with { PropertiesToRemove = [value] }; break;
            case "too-long": properties[value] = new(MaintenanceValueType.String, new string('x', 241)); break;
            default: properties[field] = new(MaintenanceValueType.String, value); break;
        }
        plan = plan with { Changes = [change with { PropertiesToSet = properties }] };
        await Assert.ThrowsAsync<ArgumentException>(() => service.CleanupAsync(new() { Plan = plan, Apply = true }));
        Assert.Equal(0, store.Writes);
    }

    [Theory]
    [InlineData("target-edit")]
    [InlineData("new-submission")]
    [InlineData("unrelated-edit")]
    public async Task Reviewed_scoring_repair_requires_the_complete_original_snapshot(string drift)
    {
        var (store, service, plan) = await ScoringSetup();
        if (drift == "new-submission") store.Put("Submissions", Row("real", "late", ("TalkSlug", "canonical")));
        else
        {
            var key = drift == "target-edit" ? ("Events", "events", "real") : ("Talks", "talks", "canonical");
            store.Put(key.Item1, store.Rows[key] with { ETag = "concurrent-user-edit" });
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CleanupAsync(new() { Plan = plan, Apply = true }));
        Assert.Equal(0, store.Writes);
    }

    [Theory]
    [InlineData("Monitor", "SubmitNow", "High")]
    [InlineData("SubmitNow", "SubmitNow", "MediumHigh")]
    [InlineData("Outreach", "SubmitNow", "Medium")]
    [InlineData("Monitor", "SubmitNow", "Low")]
    [InlineData("Monitor", "Outreach", "Medium")]
    [InlineData("SubmitNow", "Pass", "NA")]
    public async Task Reviewed_scoring_repair_accepts_supported_decisions_for_each_original_candidate(string originalCategory, string category, string priority)
    {
        var (store, service, plan) = await ScoringSetup();
        var original = store.Rows[("Events", "events", "real")];
        var originalProperties = new Dictionary<string, MaintenanceValue>(original.Properties)
        { ["Category"] = new(MaintenanceValueType.String, originalCategory) };
        originalProperties.Remove("DoNotResurface"); // Legacy absence means false, never an assignment.
        store.Put("Events", original with { Properties = originalProperties });
        var assignments = new Dictionary<string, MaintenanceValue>(plan.Changes[0].PropertiesToSet)
        {
            ["Category"] = new(MaintenanceValueType.String, category),
            ["Priority"] = new(MaintenanceValueType.String, priority),
        };
        plan = plan with
        {
            Snapshot = await service.ExportAsync(true),
            Changes = [plan.Changes[0] with { PropertiesToSet = assignments }],
        };
        await service.CleanupAsync(new() { Plan = plan, Apply = true });
        var result = store.Rows[("Events", "events", "real")];
        Assert.Equal(category, result.Properties["Category"].Value);
        Assert.Equal(priority, result.Properties["Priority"].Value);
        Assert.False(result.Properties.ContainsKey("DoNotResurface"));
    }

    [Fact]
    public async Task Reviewed_scoring_repair_hash_covers_all_assignments_and_restore_refuses_later_edits()
    {
        var (store, service, plan) = await ScoringSetup();
        var hash = MaintenanceIntegrity.PlanChecksum(plan);
        foreach (var field in plan.Changes[0].PropertiesToSet.Keys)
        {
            var properties = new Dictionary<string, MaintenanceValue>(plan.Changes[0].PropertiesToSet)
            { [field] = new(MaintenanceValueType.String, "different") };
            Assert.NotEqual(hash, MaintenanceIntegrity.PlanChecksum(plan with { Changes = [plan.Changes[0] with { PropertiesToSet = properties }] }));
        }
        await service.CleanupAsync(new() { Plan = plan, Apply = true });
        store.Put("Events", store.Rows[("Events", "events", "real")] with { ETag = "later-manual-edit" });
        var writes = store.Writes;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(new() { OperationId = plan.OperationId, Apply = true }));
        Assert.Equal(writes, store.Writes);
    }

    private static async Task<(MemoryStore Store, PipelineMaintenanceService Service, MaintenancePlan Plan)> ScoringSetup()
    {
        var (store, service, basePlan) = await Setup();
        var row = Row("events", "real", ("Name", "Real event"), ("Category", "Monitor"), ("Priority", "Low"),
            ("NextAction", "Old rationale with fictional commitment."), ("DecidedByAgent", "scoring-agent-v1"),
            ("Notes", "Keep manual notes exactly."), ("StatusDetail", "Keep manual status detail."));
        row.Properties["FutureBytes"] = new(MaintenanceValueType.Binary, "AAEC/w==");
        row.Properties["DoNotResurface"] = new(MaintenanceValueType.Boolean, "false");
        row.Properties["FamilyConflictFlag"] = new(MaintenanceValueType.Boolean, "true");
        store.Put("Events", row);
        var change = new MaintenanceChange
        {
            Table = "Events", PartitionKey = "events", RowKey = row.RowKey, ExpectedETag = row.ETag,
            Kind = MaintenanceChangeKind.ApplyScoringDecision,
            PropertiesToSet = new()
            {
                ["Category"] = new(MaintenanceValueType.String, "Monitor"),
                ["Priority"] = new(MaintenanceValueType.String, "Low"),
                ["NextAction"] = new(MaintenanceValueType.String, "Refreshed rationale after removing fictional commitments."),
                ["DecidedByAgent"] = new(MaintenanceValueType.String, "scoring-agent-v1"),
            },
        };
        return (store, service, basePlan with { Snapshot = await service.ExportAsync(true), Changes = [change] });
    }

    private static async Task<(MemoryStore Store, PipelineMaintenanceService Service, MaintenancePlan Plan)> Setup()
    {
        var store = new MemoryStore();
        store.Put("Events", Row("events", "demo", ("Name", "Fictional")));
        store.Put("Events", Row("events", "real", ("Name", "Preserve real history")));
        store.Put("Talks", Row("talks", "canonical", ("DeckUrl", "<placeholder-sharepoint-deck-url>"), ("FutureSchema", "preserve")));
        store.Put("Submissions", Row("demo", "s", ("TalkSlug", "canonical")));
        var service = new PipelineMaintenanceService(store);
        var snapshot = await service.ExportAsync(true);
        var changes = new[] { ("Events", "events", "demo"), ("Talks", "talks", "canonical"), ("Submissions", "demo", "s") }
            .Select(k => new MaintenanceChange
            {
                Table = k.Item1, PartitionKey = k.Item2, RowKey = k.Item3, ExpectedETag = store.Rows[k].ETag,
                Kind = k.Item1 == "Talks" ? MaintenanceChangeKind.RemoveProperties : MaintenanceChangeKind.Delete,
                PropertiesToRemove = k.Item1 == "Talks" ? ["DeckUrl"] : [],
            }).ToList();
        return (store, service, new MaintenancePlan { OperationId = "cleanup-1", Snapshot = snapshot, Changes = changes });
    }

    private static MaintenanceEntity Row(string partition, string key, params (string Name, string Value)[] values) => new()
    {
        PartitionKey = partition, RowKey = key, ETag = "original-" + partition + "-" + key,
        Timestamp = DateTimeOffset.Parse("2026-10-16T12:00:00Z"),
        Properties = values.ToDictionary(p => p.Name, p => new MaintenanceValue(MaintenanceValueType.String, p.Value)),
    };

    private sealed class MemoryStore(string endpoint = "https://production.table.core.windows.net") : IMaintenanceStore
    {
        public Dictionary<(string Table, string Partition, string Row), MaintenanceEntity> Rows { get; } = [];
        public MaintenanceJournal? Journal { get; private set; }
        public int Writes { get; private set; }
        public List<string> Mutations { get; } = [];
        public string? CrashAfterMutationTable { get; set; }
        public bool ThrowAfterNextAdd { get; set; }
        public int Reads { get; private set; }
        public Action<int>? BeforeRead { get; set; }
        public bool LoseLockAfterCheckpoint { get; set; }
        private bool _lockLost;
        private bool _crashCheckpoint;
        private int _journalVersion;
        public MaintenanceTarget Target { get; } = new()
        {
            TableEndpoint = endpoint,
            TableNames = MaintenanceIntegrity.TableNames.ToDictionary(n => n, n => n),
        };
        public void Put(string table, MaintenanceEntity entity) => Rows[(table, entity.PartitionKey, entity.RowKey)] = entity;
        public Task<Dictionary<string, List<MaintenanceEntity>>> ReadAllAsync(CancellationToken ct, bool allowMissingTables = false)
        {
            BeforeRead?.Invoke(++Reads);
            return Task.FromResult(MaintenanceIntegrity.TableNames.ToDictionary(n => n, n => Rows.Where(p => p.Key.Table == n).Select(p => p.Value).ToList()));
        }
        public Task<MaintenanceEntity?> GetAsync(MaintenanceChange key, CancellationToken ct)
            => Task.FromResult(Rows.GetValueOrDefault((key.Table, key.PartitionKey, key.RowKey)));
        public Task DeleteAsync(MaintenanceChange change, string etag, CancellationToken ct)
        {
            var key = (change.Table, change.PartitionKey, change.RowKey);
            Assert.Equal(etag, Rows[key].ETag);
            Rows.Remove(key);
            Mutated(key);
            return Task.CompletedTask;
        }
        public Task<string> ReplaceAsync(string table, MaintenanceEntity entity, string etag, CancellationToken ct)
        {
            var key = (table, entity.PartitionKey, entity.RowKey);
            Assert.Equal(etag, Rows[key].ETag);
            var next = entity with { ETag = "changed-" + (++Writes) };
            Rows[key] = next;
            Mutated(key, increment: false);
            return Task.FromResult(next.ETag);
        }
        public Task<string> AddAsync(string table, MaintenanceEntity entity, CancellationToken ct)
        {
            var key = (table, entity.PartitionKey, entity.RowKey);
            Assert.False(Rows.ContainsKey(key));
            var next = entity with { ETag = "added-" + (++Writes) };
            Rows.Add(key, next);
            Mutated(key, increment: false);
            if (ThrowAfterNextAdd) { ThrowAfterNextAdd = false; throw new IOException("Insert succeeded before connection was lost."); }
            return Task.FromResult(next.ETag);
        }
        private void Mutated((string Table, string Partition, string Row) key, bool increment = true)
        {
            if (increment) Writes++;
            Mutations.Add($"{key.Table}/{key.Partition}/{key.Row}");
            if (CrashAfterMutationTable == key.Table) { _crashCheckpoint = true; CrashAfterMutationTable = null; }
        }
        public Task<(MaintenanceJournal Journal, string ETag)?> ReadJournalAsync(string operationId, CancellationToken ct)
            => Task.FromResult<(MaintenanceJournal Journal, string ETag)?>(Journal is null ? null : (Clone(Journal), _journalVersion.ToString()));
        public Task<string> SaveJournalAsync(MaintenanceJournal journal, string? etag, CancellationToken ct)
        {
            if (_crashCheckpoint) { _crashCheckpoint = false; throw new IOException("Process failed before the checkpoint."); }
            Assert.Equal(Journal is null ? null : _journalVersion.ToString(), etag);
            Journal = Clone(journal);
            if (LoseLockAfterCheckpoint) _lockLost = true;
            Writes++;
            return Task.FromResult((++_journalVersion).ToString());
        }
        private static MaintenanceJournal Clone(MaintenanceJournal value) => JsonSerializer.Deserialize<MaintenanceJournal>(JsonSerializer.Serialize(value))!;
        public Task<IMaintenanceLock> AcquireLockAsync(CancellationToken ct) => Task.FromResult<IMaintenanceLock>(new NoopLock(this));
        public Task InitializeLocalRestoreAsync(CancellationToken ct) => Task.CompletedTask;
        public Task ArchiveSnapshotRestoreAsync(MaintenanceSnapshot snapshot, CancellationToken ct) => Task.CompletedTask;
        private sealed class NoopLock(MemoryStore store) : IMaintenanceLock
        {
            public CancellationToken Lost => CancellationToken.None;
            public void EnsureHeld()
            {
                if (store._lockLost) throw new OperationCanceledException("Maintenance lease was lost.");
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
