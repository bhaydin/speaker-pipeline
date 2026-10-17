using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Core;
using SpeakerPipeline.Storage;

namespace SpeakerPipeline.Storage.Tests;

public sealed class OpportunityAzuriteTests
{
    [MaintenanceAzuriteFact]
    public async Task Actionable_decision_guards_apply_without_catalog_and_idempotent_replay_precedes_new_guards()
    {
        var table = Tables().GetTableClient("O" + Guid.NewGuid().ToString("n"));
        await table.CreateAsync();
        try
        {
            var repository = new OpportunityRepository(table, TimeProvider.System);
            var original = await repository.UpsertFactsAsync(Event());
            var acceptedRequest = Score(original);
            var accepted = await repository.ApplyDecisionAsync(acceptedRequest);
            foreach (var bad in new[]
            {
                Event() with { CfpUrl = null }, Event() with { CfpUrl = "javascript:alert(1)" },
                Event() with { CfpStatus = null }, Event() with { CfpStatus = CfpStatus.Unknown },
                Event() with { CfpStatus = CfpStatus.Closed }, Event() with { CfpStatus = CfpStatus.NotYetOpen },
                Event() with { CfpStatus = CfpStatus.RevalidateLive }, Event() with { CfpDeadline = DateTimeOffset.UtcNow.AddDays(-1) },
                Event() with { FamilyConflictFlag = true },
            })
            {
                var current = (await repository.GetAsync(original.Slug))!;
                var changed = await repository.UpsertFactsAsync(bad with { ETag = current.ETag });
                await Assert.ThrowsAsync<ArgumentException>(() => repository.ApplyDecisionAsync(Score(changed)));
                Assert.Equal(Serialize(accepted), Serialize(await repository.ApplyDecisionAsync(acceptedRequest, new CatalogSnapshot())));
                Assert.Equal(changed.ETag, (await repository.GetAsync(original.Slug))!.ETag);
                if (bad.FamilyConflictFlag)
                {
                    var outreach = Score(changed);
                    await Assert.ThrowsAsync<ArgumentException>(() => repository.ApplyDecisionAsync(outreach with { Decision = outreach.Decision with { Recommendation = Recommendation.Outreach } }));
                }
            }
            var latest = (await repository.GetAsync(original.Slug))!;
            var futureDeadline = await repository.UpsertFactsAsync(Event() with { ETag = latest.ETag, CfpStatus = CfpStatus.Unknown, CfpDeadline = DateTimeOffset.UtcNow.AddDays(7) });
            Assert.Equal(Recommendation.SubmitNow, (await repository.ApplyDecisionAsync(Score(futureDeadline))).SystemVerdict);
        }
        finally { await table.DeleteAsync(); }
    }

    [MaintenanceAzuriteFact]
    public async Task Concurrent_commands_score_facts_and_feedback_use_real_atomic_transactions()
    {
        var tables = Tables();
        var table = tables.GetTableClient("O" + Guid.NewGuid().ToString("n"));
        await table.CreateAsync();
        try
        {
            var repository = new OpportunityRepository(table, TimeProvider.System);
            await Assert.ThrowsAsync<InvalidOperationException>(() => new OpportunityRepository(table, TimeProvider.System, requireMigration: true).GetAllAsync());
            var original = await repository.UpsertFactsAsync(Event());
            var submittedRequest = Action(OpportunityAction.Submitted, "submitted-once");
            var submitted = await repository.ApplyActionAsync(original.Slug, submittedRequest, original.ETag, "owner");
            Assert.Equal(OpportunityStatus.Submitted, submitted.Status);
            await Assert.ThrowsAsync<OpportunityConflictException>(() => repository.ApplyActionAsync(original.Slug, Action(OpportunityAction.Snooze7Days), original.ETag, "owner"));
            var replay = await repository.ApplyActionAsync(original.Slug, submittedRequest, original.ETag, "owner");
            Assert.Equal(submitted.ETag, replay.ETag);
            Assert.Single(await repository.GetHistoryAsync(original.Slug));
            await Assert.ThrowsAsync<ArgumentException>(() => repository.ApplyActionAsync(original.Slug, submittedRequest with { Action = OpportunityAction.Snooze7Days }, original.ETag, "owner"));

            var firstScore = Score(original);
            var scored = await repository.ApplyDecisionAsync(firstScore);
            Assert.Equal(OpportunityStatus.Submitted, scored.Status); // user move does not invalidate facts or get overwritten
            Assert.Equal(Recommendation.SubmitNow, scored.SystemVerdict);
            var projected = OpportunityMapping.Project(scored);
            var updatedFacts = await repository.UpsertFactsAsync(projected with { CfpDeadline = DateTimeOffset.UtcNow.AddDays(2), DoNotResurface = true, Category = EventCategory.Skip });
            Assert.False(updatedFacts.DoNotResurface); // whole-event PUT cannot smuggle in state edits
            Assert.Equal(OpportunityStatus.Submitted, updatedFacts.Status);
            var stale = await Assert.ThrowsAsync<OpportunityConflictException>(() => repository.ApplyDecisionAsync(Score(scored)));
            Assert.True(stale.StaleScoringInput);
            Assert.Equal(Serialize(scored), Serialize(await repository.ApplyDecisionAsync(firstScore, new CatalogSnapshot()))); // original receipt precedes changed inputs/guards
            Assert.Equal(updatedFacts.ETag, (await repository.GetAsync(original.Slug))!.ETag);

            var feedback = await repository.ApplyActionAsync(original.Slug, Action(OpportunityAction.Feedback) with { Agree = false, ReasonCode = FeedbackReason.WrongAudience }, updatedFacts.ETag, "owner");
            var item = Assert.Single(await repository.GetFeedbackAsync());
            Assert.Equal(original.Slug, item.EventSlug);
            Assert.NotNull(item.Decision);
            Assert.Equal(original.Event.CfpDeadline, item.EventSnapshot!.CfpDeadline); // facts as scored, not as feedback was entered
            var secondScore = Score(feedback);
            var rescored = await repository.ApplyDecisionAsync(secondScore with { Decision = secondScore.Decision with { Recommendation = Recommendation.Monitor } });
            var corrected = await repository.ApplyActionAsync(original.Slug, Action(OpportunityAction.Feedback) with
            { Agree = false, ReasonCode = FeedbackReason.BadTiming, SupersedesFeedbackId = item.Id, DecisionId = item.DecisionId }, rescored.ETag, "owner");
            var correction = Assert.Single(await repository.GetFeedbackAsync());
            Assert.Equal(item.DecisionId, correction.DecisionId);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(item.Decision), System.Text.Json.JsonSerializer.Serialize(correction.Decision));
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(item.EventSnapshot), System.Text.Json.JsonSerializer.Serialize(correction.EventSnapshot));
            Assert.NotEqual(rescored.LatestDecisionId, correction.DecisionId);
            var retired = await repository.ApplyActionAsync(original.Slug, Action(OpportunityAction.RetireFeedback) with { FeedbackId = correction.Id }, corrected.ETag, "owner");
            Assert.Empty(await repository.GetFeedbackAsync());
            Assert.True(retired.ScoringInputRevision > feedback.ScoringInputRevision);

            var snoozeRequest = Action(OpportunityAction.Snooze7Days, "fixed-snooze");
            var snoozed = await repository.ApplyActionAsync(original.Slug, snoozeRequest, retired.ETag, "owner");
            var snoozeReplay = await repository.ApplyActionAsync(original.Slug, snoozeRequest, retired.ETag, "owner");
            Assert.Equal(snoozed.SnoozedUntilUtc, snoozeReplay.SnoozedUntilUtc);
            var racing = await Task.WhenAll(Enumerable.Range(0, 2).Select(async index =>
            {
                try
                {
                    await repository.ApplyActionAsync(original.Slug, Action(OpportunityAction.SaveNotes) with { Note = $"Concurrent note {index}" }, snoozed.ETag, "owner");
                    return true;
                }
                catch (OpportunityConflictException) { return false; }
            }));
            Assert.Equal(1, racing.Count(won => won));
            var beforeEvidence = (await repository.GetAsync(original.Slug))!;
            var evidence = new SubmissionRecord
            {
                EventSlug = original.Slug, SubmissionId = "real-recorded-submission", EventName = "Fictional event",
                TalkSlug = "fictional-talk", TalkTitleUsed = "Actual title", AbstractUsed = "Actual recorded abstract",
                SubmittedOnUtc = DateTimeOffset.UtcNow, Status = SubmissionStatus.Accepted,
            };
            var reconciled = await repository.ReconcileSubmissionsAsync(original.Slug, [evidence], beforeEvidence.ETag);
            Assert.Equal(SubmissionStatus.Accepted, reconciled.RecordedSubmissionStatus);
            Assert.Equal(OpportunityStatus.Submitted, reconciled.Status); // explicit user placement wins
            var unchangedEvidence = await repository.ReconcileSubmissionsAsync(original.Slug, [evidence], reconciled.ETag);
            Assert.Equal(reconciled.ETag, unchangedEvidence.ETag);
            var noEvidence = await repository.ReconcileSubmissionsAsync(original.Slug, [], reconciled.ETag);
            Assert.Null(noEvidence.RecordedSubmissionStatus);
            Assert.Equal(OpportunityStatus.Submitted, noEvidence.Status); // user button is not a fabricated submission

            var longUnicodeNote = new string('\u6F22', 7900);
            var longNoteRequest = Action(OpportunityAction.SaveNotes, "long-unicode-note") with { Note = longUnicodeNote };
            var noted = await repository.ApplyActionAsync(original.Slug, longNoteRequest, noEvidence.ETag, "owner");
            Assert.Equal(longUnicodeNote, (await repository.GetAsync(original.Slug))!.UserNotes);
            Assert.Equal(longUnicodeNote, (await repository.GetHistoryAsync(original.Slug)).First(h => h.Reason == longUnicodeNote).Reason);
            Assert.True((await table.GetEntityAsync<TableEntity>(original.Slug, "current")).Value.GetInt32("OpportunityJsonChunkCount") > 1);
            Assert.Equal(noted.ETag, (await repository.ApplyActionAsync(original.Slug, longNoteRequest, noEvidence.ETag, "owner")).ETag);
        }
        finally { await table.DeleteAsync(); }
    }

    [MaintenanceAzuriteFact]
    public async Task Duplicate_commands_replay_original_state_and_version_after_later_changes_without_mutating_current()
    {
        var table = Tables().GetTableClient("O" + Guid.NewGuid().ToString("n"));
        await table.CreateAsync();
        try
        {
            // A pre-version aggregate still exposes its Azure ETag until its next domain write.
            var imported = OpportunityMapping.Import(Event());
            await table.AddEntityAsync(OpportunityRepository.Write(new TableEntity(imported.Slug, "current"), imported));
            var physicalBefore = (await table.GetEntityAsync<TableEntity>(imported.Slug, "current")).Value;
            var repository = new OpportunityRepository(table, TimeProvider.System);
            var original = (await repository.GetAsync(imported.Slug))!;
            Assert.Equal(physicalBefore.ETag.ToString(), original.ETag);

            var request = Action(OpportunityAction.Snooze7Days, "original-snooze");
            var accepted = await repository.ApplyActionAsync(original.Slug, request, original.ETag, "owner");
            var physicalAccepted = (await table.GetEntityAsync<TableEntity>(original.Slug, "current")).Value;
            Assert.NotEqual(physicalAccepted.ETag.ToString(), accepted.ETag);
            Assert.StartsWith("\"", accepted.ETag);
            Assert.EndsWith("\"", accepted.ETag);
            Assert.Equal(Serialize(accepted), Serialize(await repository.GetAsync(original.Slug)));

            var facts = await repository.UpsertFactsAsync(OpportunityMapping.Project(accepted) with { Name = "Changed event name" });
            var later = await repository.ApplyActionAsync(original.Slug, Action(OpportunityAction.Submitted), facts.ETag, "owner");
            var physicalLater = (await table.GetEntityAsync<TableEntity>(original.Slug, "current")).Value.ETag;
            var historyCount = (await repository.GetHistoryAsync(original.Slug)).Count;
            var replay = await repository.ApplyActionAsync(original.Slug, request, original.ETag, "owner");

            Assert.Equal(Serialize(accepted), Serialize(replay));
            Assert.Equal(Serialize(later), Serialize(await repository.GetAsync(original.Slug)));
            Assert.Equal(physicalLater, (await table.GetEntityAsync<TableEntity>(original.Slug, "current")).Value.ETag);
            Assert.Equal(historyCount, (await repository.GetHistoryAsync(original.Slug)).Count);
            var stale = await Assert.ThrowsAsync<OpportunityConflictException>(() => repository.ApplyActionAsync(original.Slug,
                Action(OpportunityAction.SaveNotes) with { Note = "Stale replay version" }, replay.ETag, "owner"));
            Assert.Equal(later.ETag, stale.Current!.ETag);

            // Old receipts had no version. Preserve their exact stored result, never invent a newer one.
            var command = (await table.GetEntityAsync<TableEntity>(original.Slug, "command-" + Hash(request.IdempotencyKey))).Value;
            OpportunityRepository.WritePayload(command, "Result", Serialize(accepted with { ETag = "" }));
            await table.UpdateEntityAsync(command, command.ETag, TableUpdateMode.Replace);
            Assert.Equal(Serialize(accepted with { ETag = "" }), Serialize(await repository.ApplyActionAsync(original.Slug, request, original.ETag, "owner")));
            Assert.Equal(later.ETag, (await repository.GetAsync(original.Slug))!.ETag);
        }
        finally { await table.DeleteAsync(); }
    }

    [MaintenanceAzuriteFact]
    public async Task Frozen_migration_is_previewable_resumable_lossless_and_snapshot_compatible()
    {
        var tables = Tables();
        var connection = Environment.GetEnvironmentVariable("PIPELINE_AZURITE_CONNECTION_STRING")!;
        var suffix = Guid.NewGuid().ToString("n");
        var target = new MaintenanceTarget
        {
            TableEndpoint = tables.Uri.AbsoluteUri.TrimEnd('/'),
            TableNames = MaintenanceIntegrity.TableNames.Append("Opportunities").ToDictionary(n => n, n => n + suffix),
        };
        var container = new BlobServiceClient(connection).GetBlobContainerClient("migration" + suffix);
        var store = new AzureMaintenanceStore(tables, container, target);
        await store.InitializeLocalRestoreAsync(CancellationToken.None);
        try
        {
            var source = tables.GetTableClient(target.TableNames["Events"]);
            var raw = Mapping.ToEntity(Event() with { Category = EventCategory.Skip, DoNotResurface = false });
            raw["UnknownBinary"] = new byte[] { 1, 3, 5 };
            raw["Kind"] = "legacy-unknown-value";
            await source.AddEntityAsync(raw);
            var snapshot = await new PipelineMaintenanceService(store).ExportAsync(true);
            Assert.Equal(2, snapshot.Version);
            var settings = Options.Create(new StorageOptions { OpportunitiesProvisioned = true, OpportunitiesTableName = target.TableNames["Opportunities"] });
            var migration = new OpportunityMigrationService(tables, store, settings);
            await Assert.ThrowsAsync<InvalidOperationException>(() => migration.MigrateAsync(new() { Snapshot = snapshot }, false));
            var repairedSource = (await source.GetEntityAsync<TableEntity>("events", raw.RowKey)).Value;
            repairedSource.Remove("Kind");
            await source.UpdateEntityAsync(repairedSource, repairedSource.ETag, TableUpdateMode.Replace);
            snapshot = await new PipelineMaintenanceService(store).ExportAsync(true);
            var request = new OpportunityMigrationRequest { Snapshot = snapshot };
            var preview = await migration.MigrateAsync(request, false);
            Assert.Equal(1, preview.SourceCount);
            Assert.False(preview.Applied);
            await Assert.ThrowsAsync<InvalidOperationException>(() => migration.MigrateAsync(request with { Apply = true }, false));
            var copied = await migration.MigrateAsync(request with { Apply = true }, true);
            Assert.Equal(1, copied.CreatedCount);
            var replay = await migration.MigrateAsync(request with { Apply = true }, true);
            Assert.Equal(1, replay.ExistingCount);
            var destination = tables.GetTableClient(target.TableNames["Opportunities"]);
            var aggregate = (await destination.GetEntityAsync<TableEntity>(raw.RowKey, "current")).Value;
            Assert.Equal(new byte[] { 1, 3, 5 }, aggregate.GetBinary("UnknownBinary"));
            aggregate["UnknownBinary"] = new byte[] { 9 };
            await destination.UpdateEntityAsync(aggregate, aggregate.ETag, TableUpdateMode.Replace);
            await Assert.ThrowsAsync<InvalidOperationException>(() => migration.MigrateAsync(request, false));
            aggregate = (await destination.GetEntityAsync<TableEntity>(raw.RowKey, "current")).Value;
            aggregate["UnknownBinary"] = new byte[] { 1, 3, 5 };
            await destination.UpdateEntityAsync(aggregate, aggregate.ETag, TableUpdateMode.Replace);
            var audit = (await destination.GetEntityAsync<TableEntity>(raw.RowKey, "history-migration")).Value;
            await destination.DeleteEntityAsync(raw.RowKey, "history-migration", audit.ETag);
            await Assert.ThrowsAsync<InvalidOperationException>(() => migration.MigrateAsync(request, false));
            await destination.AddEntityAsync(new TableEntity(audit.ToDictionary(p => p.Key, p => p.Value)));
            var repository = new OpportunityRepository(destination, TimeProvider.System, requireMigration: true);
            var opportunity = (await repository.GetAsync(raw.RowKey))!;
            Assert.Equal(OpportunityStatus.Dropped, opportunity.Status);
            Assert.False(opportunity.DoNotResurface);
            var projected = OpportunityMapping.Project(opportunity);
            var updated = await repository.UpsertFactsAsync(projected with { Name = "Updated factual name" });
            Assert.NotEqual(opportunity.ETag, updated.ETag);
            var noteRequest = Action(OpportunityAction.SaveNotes, "snapshot-replay");
            var noted = await repository.ApplyActionAsync(opportunity.Slug, noteRequest, updated.ETag, "owner");
            Assert.Equal(new byte[] { 1, 3, 5 }, (await destination.GetEntityAsync<TableEntity>(raw.RowKey, "current")).Value.GetBinary("UnknownBinary"));
            Assert.Equal("Fictional event", (await source.GetEntityAsync<TableEntity>("events", raw.RowKey)).Value.GetString("Name"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => migration.MigrateAsync(request with { Apply = true }, true));
            var full = await new PipelineMaintenanceService(store).ExportAsync(true);
            Assert.Contains("Opportunities", full.Tables.Keys);
            await RestoreRoundTripAsync(full, tables, connection, async restored =>
            {
                var restoredRepository = new OpportunityRepository(restored, TimeProvider.System, requireMigration: true);
                Assert.Equal(Serialize(noted), Serialize(await restoredRepository.GetAsync(opportunity.Slug)));
                Assert.Equal(Serialize(noted), Serialize(await restoredRepository.ApplyActionAsync(opportunity.Slug, noteRequest, updated.ETag, "owner")));
                var changed = await restoredRepository.ApplyActionAsync(opportunity.Slug, Action(OpportunityAction.SaveNotes) with { Note = "After restore" }, noted.ETag, "owner");
                Assert.NotEqual(noted.ETag, changed.ETag);
            });
            var legacy = full with
            {
                Version = 1,
                Target = full.Target with { TableNames = full.Target.TableNames.Where(p => p.Key != "Opportunities").ToDictionary(p => p.Key, p => p.Value) },
                Tables = full.Tables.Where(p => p.Key != "Opportunities").ToDictionary(p => p.Key, p => p.Value),
            };
            legacy = legacy with { Checksum = MaintenanceIntegrity.SnapshotChecksum(legacy) };
            await RestoreRoundTripAsync(legacy, tables, connection);
        }
        finally
        {
            foreach (var name in target.TableNames.Values) await tables.DeleteTableAsync(name);
            await container.DeleteIfExistsAsync();
        }
    }

    private static TableServiceClient Tables()
    {
        var client = new TableServiceClient(Environment.GetEnvironmentVariable("PIPELINE_AZURITE_CONNECTION_STRING")!);
        Assert.True(client.Uri.IsLoopback);
        return client;
    }
    private static async Task RestoreRoundTripAsync(MaintenanceSnapshot snapshot, TableServiceClient tables, string connection, Func<TableClient, Task>? verify = null)
    {
        var suffix = Guid.NewGuid().ToString("n");
        var target = new MaintenanceTarget
        {
            TableEndpoint = tables.Uri.AbsoluteUri.TrimEnd('/'),
            TableNames = MaintenanceIntegrity.TableNames.Append("Opportunities").ToDictionary(n => n, n => n + suffix),
        };
        var container = new BlobServiceClient(connection).GetBlobContainerClient("restore" + suffix);
        var store = new AzureMaintenanceStore(tables, container, target);
        try
        {
            var service = new PipelineMaintenanceService(store);
            var result = await service.RestoreSnapshotAsync(new() { Snapshot = snapshot, Apply = true }, allowLocalRehearsal: true);
            Assert.True(result.Restored);
            var restored = await service.ExportAsync(true);
            foreach (var table in snapshot.Tables)
                Assert.Equal(table.Value.Select(MaintenanceIntegrity.EntityChecksum).Order(), restored.Tables[table.Key].Select(MaintenanceIntegrity.EntityChecksum).Order());
            if (verify is not null) await verify(tables.GetTableClient(target.TableNames["Opportunities"]));
        }
        finally
        {
            foreach (var name in target.TableNames.Values) await tables.DeleteTableAsync(name);
            await container.DeleteIfExistsAsync();
        }
    }
    private static string Serialize(OpportunityRecord? record) => System.Text.Json.JsonSerializer.Serialize(record, OpportunityRepository.Json);
    private static string Hash(string value) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    private static EventRecord Event() => new() { Slug = "fictional-event", Name = "Fictional event", Category = EventCategory.Monitor, Priority = Priority.Low, EventType = EventType.Conference, CfpUrl = "https://example.invalid/cfp", CfpStatus = CfpStatus.Open };
    private static OpportunityActionRequest Action(OpportunityAction action, string? id = null) => new() { Action = action, IdempotencyKey = id ?? Guid.NewGuid().ToString("n") };
    private static OpportunityScoringRequest Score(OpportunityRecord prior) => new()
    {
        ExpectedETag = prior.ETag, ScoringInputRevision = prior.ScoringInputRevision, IdempotencyKey = Guid.NewGuid().ToString("n"),
        Decision = new() { EventSlug = prior.Slug, Recommendation = Recommendation.SubmitNow, Rationale = "A concrete catalog match", FitScore = 8, EffortScore = 3, ConfidenceScore = 8 },
    };
}
