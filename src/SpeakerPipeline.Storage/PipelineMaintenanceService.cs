using SpeakerPipeline.Core;

namespace SpeakerPipeline.Storage;

/// <summary>Conditional, journaled maintenance. All originals are durable before the first business write.</summary>
internal sealed class PipelineMaintenanceService(IMaintenanceStore store) : IPipelineMaintenanceService
{
    public async Task<MaintenanceSnapshot> ExportAsync(bool writersFrozen, CancellationToken ct = default)
    {
        var rows = await store.ReadAllAsync(ct).ConfigureAwait(false);
        if (writersFrozen)
        {
            var first = Flatten(rows);
            var second = Flatten(await store.ReadAllAsync(ct).ConfigureAwait(false));
            if (first.Count != second.Count || first.Any(p => !second.TryGetValue(p.Key, out var row) || !SameOriginal(p.Value, row)))
                throw new InvalidOperationException("Storage changed during frozen export. Drain in-flight writers before exporting again.");
        }
        var snapshot = new MaintenanceSnapshot
        {
            SnapshotId = Guid.NewGuid().ToString("n"), CreatedUtc = DateTimeOffset.UtcNow,
            WritersFrozen = writersFrozen, Target = store.Target, Tables = rows,
        };
        return snapshot with { Checksum = MaintenanceIntegrity.SnapshotChecksum(snapshot) };
    }

    public async Task<MaintenanceResult> CleanupAsync(MaintenanceApplyRequest request, CancellationToken ct = default)
    {
        var plan = request.Plan;
        ValidatePlan(plan);
        if (request.Apply && !plan.Snapshot.WritersFrozen)
            throw new ArgumentException("Apply requires a new snapshot exported while business writes were frozen.");

        if (!request.Apply)
        {
            var existing = await store.ReadJournalAsync(plan.OperationId, ct).ConfigureAwait(false);
            var journal = existing?.Journal ?? NewJournal(plan);
            ValidateJournalPlan(journal, plan);
            await ValidateCleanupStateAsync(journal, ct).ConfigureAwait(false);
            return Result(journal, applied: false, preview: true);
        }

        await using var operationLock = await store.AcquireLockAsync(ct).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, operationLock.Lost);
        ct = linked.Token;
        var saved = await store.ReadJournalAsync(plan.OperationId, ct).ConfigureAwait(false);
        var operation = saved?.Journal ?? NewJournal(plan);
        ValidateJournalPlan(operation, plan);
        await ValidateCleanupStateAsync(operation, ct).ConfigureAwait(false);
        var journalETag = saved?.ETag ?? await CheckpointAsync(operationLock, operation, null, ct).ConfigureAwait(false);

        foreach (var item in operation.Items)
        {
            ct.ThrowIfCancellationRequested();
            operationLock.EnsureHeld();
            if (item.State == "Applied") continue;
            var before = Original(plan, item.Change);
            var current = await store.GetAsync(item.Change, ct).ConfigureAwait(false);
            if (item.State == "Executing" && MatchesAfter(item.Change, before, current))
            {
                item.AfterETag = current?.ETag;
                item.State = "Applied";
                journalETag = await CheckpointAsync(operationLock, operation, journalETag, ct).ConfigureAwait(false);
                continue;
            }
            RequireOriginal(before, current);
            item.State = "Executing";
            journalETag = await CheckpointAsync(operationLock, operation, journalETag, ct).ConfigureAwait(false);
            operationLock.EnsureHeld();
            if (item.Change.Kind == MaintenanceChangeKind.Delete)
            {
                await store.DeleteAsync(item.Change, before.ETag, ct).ConfigureAwait(false);
            }
            else
            {
                item.AfterETag = await store.ReplaceAsync(item.Change.Table, Repaired(before, item.Change), before.ETag, ct).ConfigureAwait(false);
            }
            item.State = "Applied";
            journalETag = await CheckpointAsync(operationLock, operation, journalETag, ct).ConfigureAwait(false);
        }
        return Result(operation, applied: true);
    }

    public async Task<MaintenanceResult> RestoreAsync(MaintenanceRestoreRequest request, CancellationToken ct = default)
    {
        ValidateOperationId(request.OperationId);
        if (!request.Apply)
        {
            var saved = await store.ReadJournalAsync(request.OperationId, ct).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("Maintenance operation was not found.");
            ValidatePlan(saved.Journal.Plan);
            ValidateJournalPlan(saved.Journal, saved.Journal.Plan);
            await ValidateRestoreStateAsync(saved.Journal, ct).ConfigureAwait(false);
            return Result(saved.Journal, applied: false, preview: true);
        }

        await using var operationLock = await store.AcquireLockAsync(ct).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, operationLock.Lost);
        ct = linked.Token;
        var stored = await store.ReadJournalAsync(request.OperationId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Maintenance operation was not found.");
        var journal = stored.Journal;
        ValidatePlan(journal.Plan);
        ValidateJournalPlan(journal, journal.Plan);
        await ValidateRestoreStateAsync(journal, ct).ConfigureAwait(false);
        if (journal.Restored) return Result(journal, applied: true);
        journal.Restoring = true;
        var journalETag = await CheckpointAsync(operationLock, journal, stored.ETag, ct).ConfigureAwait(false);

        // Restore parents before children; only rows named by this operation may change.
        foreach (var item in journal.Items.AsEnumerable().Reverse())
        {
            ct.ThrowIfCancellationRequested();
            operationLock.EnsureHeld();
            if (item.State is "Restored" or "Unchanged") continue;
            var before = Original(journal.Plan, item.Change);
            var current = await store.GetAsync(item.Change, ct).ConfigureAwait(false);
            if (item.State == "Planned" || (item.State == "Executing" && SameOriginal(before, current)))
            {
                RequireOriginal(before, current);
                item.State = "Unchanged";
            }
            else if (item.State == "Restoring" && SameLogical(before, current))
            {
                item.RestoredETag = current!.ETag;
                item.State = "Restored";
            }
            else
            {
                RequireAfter(item, before, current);
                item.State = "Restoring";
                item.AfterETag ??= current?.ETag;
                journalETag = await CheckpointAsync(operationLock, journal, journalETag, ct).ConfigureAwait(false);
                operationLock.EnsureHeld();
                item.RestoredETag = current is null
                    ? await store.AddAsync(item.Change.Table, before, ct).ConfigureAwait(false)
                    : await store.ReplaceAsync(item.Change.Table, before, current.ETag, ct).ConfigureAwait(false);
                item.State = "Restored";
            }
            journalETag = await CheckpointAsync(operationLock, journal, journalETag, ct).ConfigureAwait(false);
        }
        journal.Restored = true;
        await CheckpointAsync(operationLock, journal, journalETag, ct).ConfigureAwait(false);
        return Result(journal, applied: true);
    }

    public async Task<MaintenanceResult?> GetOperationAsync(string operationId, CancellationToken ct = default)
    {
        ValidateOperationId(operationId);
        var saved = await store.ReadJournalAsync(operationId, ct).ConfigureAwait(false);
        if (saved is not null) ValidateJournalPlan(saved.Value.Journal, saved.Value.Journal.Plan);
        return saved is null ? null : Result(saved.Value.Journal, applied: false);
    }

    public async Task<MaintenanceResult> RestoreSnapshotAsync(MaintenanceSnapshotRestoreRequest request, bool allowLocalRehearsal = false, CancellationToken ct = default)
    {
        var snapshot = request.Snapshot;
        var local = allowLocalRehearsal && new Uri(store.Target.TableEndpoint).IsLoopback;
        ValidateSnapshot(snapshot, allowDifferentTarget: local);
        if (request.Apply && !snapshot.WritersFrozen)
            throw new ArgumentException("Restore requires a consistent snapshot exported while business writes were frozen.");
        var expected = Flatten(snapshot.Tables);
        var current = Flatten(await store.ReadAllAsync(ct, allowMissingTables: local).ConfigureAwait(false));
        ValidateCreateOnlyRestore(expected, current);
        if (!request.Apply) return SnapshotRestoreResult(snapshot, current.Count, applied: false);

        // Only an explicitly authorized local rehearsal can create resources. Production uses IaC.
        if (local) await store.InitializeLocalRestoreAsync(ct).ConfigureAwait(false);
        await using var operationLock = await store.AcquireLockAsync(ct).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, operationLock.Lost);
        ct = linked.Token;
        current = Flatten(await store.ReadAllAsync(ct).ConfigureAwait(false));
        ValidateCreateOnlyRestore(expected, current);
        operationLock.EnsureHeld();
        await store.ArchiveSnapshotRestoreAsync(snapshot, ct).ConfigureAwait(false);
        foreach (var name in new[] { "Talks", "Events", "Topics", "Blackouts", "Submissions", "NotificationLog" })
        {
            foreach (var row in snapshot.Tables[name])
            {
                ct.ThrowIfCancellationRequested();
                operationLock.EnsureHeld();
                if (current.ContainsKey((name, row.PartitionKey, row.RowKey))) continue;
                await store.AddAsync(name, row, ct).ConfigureAwait(false);
            }
        }
        var restored = Flatten(await store.ReadAllAsync(ct).ConfigureAwait(false));
        ValidateCreateOnlyRestore(expected, restored);
        if (restored.Count != expected.Count) throw new InvalidOperationException("Snapshot restore verification found missing rows.");
        return SnapshotRestoreResult(snapshot, restored.Count, applied: true);
    }

    private static void ValidateCreateOnlyRestore(
        Dictionary<(string Table, string Partition, string Row), MaintenanceEntity> expected,
        Dictionary<(string Table, string Partition, string Row), MaintenanceEntity> current)
    {
        foreach (var (key, row) in current)
            if (!expected.TryGetValue(key, out var original) || !SameLogical(original, row))
                throw new InvalidOperationException("Snapshot restore is create-only: destination contains extra or conflicting rows. Nothing will be overwritten.");
    }

    private Task<string> CheckpointAsync(IMaintenanceLock operationLock, MaintenanceJournal journal, string? etag, CancellationToken ct)
    {
        operationLock.EnsureHeld();
        return store.SaveJournalAsync(journal, etag, ct);
    }

    private static MaintenanceResult SnapshotRestoreResult(MaintenanceSnapshot snapshot, int completed, bool applied) => new()
    {
        OperationId = "restore-" + snapshot.Checksum[..32], SnapshotChecksum = snapshot.Checksum,
        Status = applied ? "Restored" : "Preview", ChangeCount = snapshot.Tables.Values.Sum(rows => rows.Count),
        CompletedCount = completed, Applied = applied, Restored = applied,
    };

    private void ValidatePlan(MaintenancePlan plan)
    {
        ValidateOperationId(plan.OperationId);
        var snapshot = plan.Snapshot;
        ValidateSnapshot(snapshot);
        ValidateChanges(plan);
    }

    private void ValidateSnapshot(MaintenanceSnapshot snapshot, bool allowDifferentTarget = false)
    {
        if (snapshot.Version != 1 || string.IsNullOrWhiteSpace(snapshot.SnapshotId) || snapshot.CreatedUtc == default)
            throw new ArgumentException("Unsupported or incomplete maintenance snapshot.");
        if (snapshot.Checksum != MaintenanceIntegrity.SnapshotChecksum(snapshot))
            throw new ArgumentException("Snapshot checksum does not match its contents.");
        if (!allowDifferentTarget && (snapshot.Target.TableEndpoint != store.Target.TableEndpoint ||
            snapshot.Target.TableNames.Count != store.Target.TableNames.Count ||
            store.Target.TableNames.Any(p => !snapshot.Target.TableNames.TryGetValue(p.Key, out var name) || name != p.Value)))
            throw new ArgumentException("Snapshot belongs to a different storage target.");
        if (snapshot.Tables.Count != MaintenanceIntegrity.TableNames.Length || MaintenanceIntegrity.TableNames.Any(n => !snapshot.Tables.ContainsKey(n)))
            throw new ArgumentException("A complete snapshot of all six business tables is required.");
        foreach (var table in snapshot.Tables)
        {
            if (table.Value.Select(Key).Distinct().Count() != table.Value.Count)
                throw new ArgumentException("Snapshot contains duplicate entity keys.");
            foreach (var row in table.Value)
            {
                if (string.IsNullOrWhiteSpace(row.ETag) || row.ETag == "*")
                    throw new ArgumentException("Every snapshot row must have an exact ETag.");
                _ = MaintenanceRawMapping.ToEntity(row); // Reject invalid typed values before any write.
            }
        }
    }

    private static void ValidateChanges(MaintenancePlan plan)
    {
        if (plan.Changes.Select(Key).Distinct().Count() != plan.Changes.Count)
            throw new ArgumentException("A row may appear only once in a cleanup plan.");
        foreach (var change in plan.Changes)
        {
            if (!Enum.IsDefined(change.Kind)) throw new ArgumentException("Unknown cleanup change kind.");
            var before = Original(plan, change);
            if (change.ExpectedETag != before.ETag || change.ExpectedETag == "*")
                throw new ArgumentException("Every change must name its exact snapshot ETag.");
            if (change.Kind == MaintenanceChangeKind.Delete && change.PropertiesToRemove.Count != 0)
                throw new ArgumentException("Delete changes cannot include property repairs.");
            if (change.Kind != MaintenanceChangeKind.SetProperties && change.PropertiesToSet.Count != 0)
                throw new ArgumentException("Property assignments require an explicit SetProperties change.");
            if (change.Kind == MaintenanceChangeKind.RemoveProperties &&
                (change.PropertiesToRemove.Count == 0 || change.PropertiesToRemove.Any(p => !before.Properties.ContainsKey(p))))
                throw new ArgumentException("Repairs must name existing user properties explicitly.");
            if (change.Kind == MaintenanceChangeKind.SetProperties)
            {
                if (change.Table != "Events" || change.PartitionKey != "events" || change.PropertiesToRemove.Count != 0 ||
                    change.PropertiesToSet.Count == 0 || change.PropertiesToSet.Any(p =>
                        p.Key is not ("FamilyConflictFlag" or "PrepConflictFlag") ||
                        p.Value.Type != MaintenanceValueType.Boolean || p.Value.Value is not ("true" or "false")))
                    throw new ArgumentException("SetProperties is restricted to boolean Events conflict flags; decisions, content, and references cannot be changed.");
                if (change.PropertiesToSet.All(p => before.Properties.TryGetValue(p.Key, out var old) && old == p.Value))
                    throw new ArgumentException("SetProperties must change at least one derived flag.");
            }
        }
        var projected = Flatten(plan.Snapshot.Tables);
        foreach (var change in plan.Changes)
        {
            if (change.Kind == MaintenanceChangeKind.Delete) projected.Remove(Key(change));
            else projected[Key(change)] = Repaired(Original(plan, change), change);
        }
        ValidateReferences(projected);
    }

    private async Task ValidateCleanupStateAsync(MaintenanceJournal journal, CancellationToken ct)
    {
        if (journal.Restoring || journal.Restored) throw new InvalidOperationException("This cleanup has entered restore; it cannot be applied again.");
        var original = Flatten(journal.Plan.Snapshot.Tables);
        var current = Flatten(await store.ReadAllAsync(ct).ConfigureAwait(false));
        var changes = journal.Items.ToDictionary(i => Key(i.Change));
        foreach (var (key, before) in original)
        {
            current.TryGetValue(key, out var now);
            if (!changes.TryGetValue(key, out var item) || item.State == "Planned") RequireOriginal(before, now);
            else if (item.State == "Executing")
            {
                if (!SameOriginal(before, now) && !MatchesAfter(item.Change, before, now))
                    throw Conflict(before);
            }
            else if (item.State == "Applied") RequireAfter(item, before, now);
            else throw new InvalidOperationException("Unexpected journal state.");
        }
        if (current.Keys.Any(k => !original.ContainsKey(k)))
            throw new InvalidOperationException("Rows were added after export. Freeze all writers and create a fresh snapshot.");
    }

    private async Task ValidateRestoreStateAsync(MaintenanceJournal journal, CancellationToken ct)
    {
        var current = Flatten(await store.ReadAllAsync(ct).ConfigureAwait(false));
        foreach (var item in journal.Items)
        {
            var before = Original(journal.Plan, item.Change);
            current.TryGetValue(Key(item.Change), out var now);
            switch (item.State)
            {
                case "Planned": case "Unchanged": RequireOriginal(before, now); break;
                case "Executing" when SameOriginal(before, now): break;
                case "Restoring" when SameLogical(before, now): break;
                case "Restored":
                    if (!SameLogical(before, now) || now!.ETag != item.RestoredETag) throw Conflict(before);
                    break;
                default: RequireAfter(item, before, now); break;
            }
            current[Key(item.Change)] = before;
        }
        ValidateReferences(current);
    }

    private static void ValidateReferences(Dictionary<(string Table, string Partition, string Row), MaintenanceEntity> rows)
    {
        foreach (var (key, submission) in rows.Where(p => p.Key.Table == "Submissions"))
        {
            if (!rows.ContainsKey(("Events", "events", key.Partition)))
                throw new InvalidOperationException($"Submission '{key.Partition}/{key.Row}' would have no event.");
            if (!submission.Properties.TryGetValue("TalkSlug", out var talk) || talk.Type != MaintenanceValueType.String ||
                !rows.ContainsKey(("Talks", "talks", talk.Value)))
                throw new InvalidOperationException($"Submission '{key.Partition}/{key.Row}' would have no talk.");
        }
    }

    private static MaintenanceJournal NewJournal(MaintenancePlan plan) => new()
    {
        Plan = plan, PlanChecksum = MaintenanceIntegrity.PlanChecksum(plan),
        Items = plan.Changes.OrderBy(c => c.Table switch { "Submissions" => 0, "NotificationLog" => 1, "Topics" => 2, "Blackouts" => 3, "Events" => 4, "Talks" => 5, _ => 6 })
            .ThenBy(c => c.PartitionKey, StringComparer.Ordinal).ThenBy(c => c.RowKey, StringComparer.Ordinal)
            .Select(c => new MaintenanceJournalItem { Change = c }).ToList(),
    };

    private static void ValidateJournalPlan(MaintenanceJournal journal, MaintenancePlan plan)
    {
        if (journal.PlanChecksum != MaintenanceIntegrity.PlanChecksum(journal.Plan) ||
            journal.PlanChecksum != MaintenanceIntegrity.PlanChecksum(journal.Plan with { Changes = journal.Items.Select(i => i.Change).ToList() }))
            throw new InvalidOperationException("Maintenance journal integrity verification failed.");
        if (journal.PlanChecksum != MaintenanceIntegrity.PlanChecksum(plan))
            throw new InvalidOperationException("Operation ID is already bound to another immutable plan.");
    }

    private static MaintenanceResult Result(MaintenanceJournal journal, bool applied, bool preview = false) => new()
    {
        OperationId = journal.Plan.OperationId, SnapshotChecksum = journal.Plan.Snapshot.Checksum,
        ChangeCount = journal.Items.Count,
        CompletedCount = journal.Items.Count(i => i.State is "Applied" or "Restored" or "Unchanged"),
        Applied = applied, Restored = journal.Restored,
        Status = preview ? "Preview" : journal.Restored ? "Restored" : journal.Restoring ? "Restoring"
            : journal.Items.All(i => i.State == "Applied") ? "Completed" : "InProgress",
    };

    private static void ValidateOperationId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Operation ID must contain 1–64 ASCII letters, digits, or hyphens.");
    }
    private static MaintenanceEntity Original(MaintenancePlan plan, MaintenanceChange change)
        => plan.Snapshot.Tables.TryGetValue(change.Table, out var rows)
            ? rows.SingleOrDefault(r => r.PartitionKey == change.PartitionKey && r.RowKey == change.RowKey)
                ?? throw new ArgumentException("Change key was not found in the snapshot.")
            : throw new ArgumentException("Change names an unknown business table.");
    private static MaintenanceEntity Repaired(MaintenanceEntity before, MaintenanceChange change)
    {
        var properties = before.Properties.Where(p => !change.PropertiesToRemove.Contains(p.Key, StringComparer.Ordinal))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        foreach (var property in change.PropertiesToSet) properties[property.Key] = property.Value;
        return before with { Properties = properties };
    }
    private static bool SameLogical(MaintenanceEntity before, MaintenanceEntity? current)
        => current is not null && MaintenanceIntegrity.EntityChecksum(before) == MaintenanceIntegrity.EntityChecksum(current);
    private static bool SameOriginal(MaintenanceEntity before, MaintenanceEntity? current)
        => current?.ETag == before.ETag && current.Timestamp == before.Timestamp && SameLogical(before, current);
    private static void RequireOriginal(MaintenanceEntity before, MaintenanceEntity? current)
    {
        if (!SameOriginal(before, current)) throw Conflict(before);
    }
    private static bool MatchesAfter(MaintenanceChange change, MaintenanceEntity before, MaintenanceEntity? current)
        => change.Kind == MaintenanceChangeKind.Delete ? current is null : SameLogical(Repaired(before, change), current);
    private static void RequireAfter(MaintenanceJournalItem item, MaintenanceEntity before, MaintenanceEntity? current)
    {
        if (!MatchesAfter(item.Change, before, current) || (item.AfterETag is not null && current?.ETag != item.AfterETag)) throw Conflict(before);
    }
    private static InvalidOperationException Conflict(MaintenanceEntity before)
        => new($"Entity '{before.PartitionKey}/{before.RowKey}' changed since the guarded snapshot or operation. No overwrite is permitted.");
    private static (string Partition, string Row) Key(MaintenanceEntity e) => (e.PartitionKey, e.RowKey);
    private static (string Table, string Partition, string Row) Key(MaintenanceChange c) => (c.Table, c.PartitionKey, c.RowKey);
    private static Dictionary<(string Table, string Partition, string Row), MaintenanceEntity> Flatten(Dictionary<string, List<MaintenanceEntity>> tables)
        => tables.SelectMany(t => t.Value.Select(e => (Key: (Table: t.Key, Partition: e.PartitionKey, Row: e.RowKey), Entity: e))).ToDictionary(p => p.Key, p => p.Entity);
}
