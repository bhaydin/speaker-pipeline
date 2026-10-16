using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Storage;

internal sealed class OpportunityMigrationService(TableServiceClient tables, IMaintenanceStore maintenance, IOptions<StorageOptions> options) : IOpportunityMigrationService
{
    public async Task<OpportunityMigrationResult> MigrateAsync(OpportunityMigrationRequest request, bool writersFrozen, CancellationToken ct = default)
    {
        var settings = options.Value;
        if (settings.OpportunitiesEnabled) throw new InvalidOperationException("Migration is forbidden after opportunity authority is enabled.");
        if (!settings.OpportunitiesProvisioned) throw new InvalidOperationException("Provision Opportunities and enable its snapshot inventory before migration.");
        var snapshot = request.Snapshot;
        if (snapshot.Version is not (1 or 2) || snapshot.Checksum != MaintenanceIntegrity.SnapshotChecksum(snapshot))
            throw new ArgumentException("Migration requires a valid checksummed snapshot.");
        if (!snapshot.WritersFrozen || (request.Apply && !writersFrozen))
            throw new InvalidOperationException("Migration requires a frozen snapshot and apply requires the current writer freeze.");
        if (snapshot.Target.TableEndpoint != maintenance.Target.TableEndpoint ||
            MaintenanceIntegrity.TableNames.Any(name => !snapshot.Target.TableNames.TryGetValue(name, out var physical) || physical != maintenance.Target.TableNames[name]))
            throw new ArgumentException("Snapshot belongs to a different source storage target.");
        if (snapshot.Target.TableNames.TryGetValue("Opportunities", out var targetTable) && targetTable != settings.OpportunitiesTableName)
            throw new ArgumentException("Snapshot targets a different Opportunities table.");
        if (MaintenanceIntegrity.TableNames.Any(name => !snapshot.Tables.ContainsKey(name))) throw new ArgumentException("A complete legacy snapshot is required.");

        if (!request.Apply) return await CopyAsync(snapshot, false, ct).ConfigureAwait(false);
        await using var operationLock = await maintenance.AcquireLockAsync(ct).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, operationLock.Lost);
        operationLock.EnsureHeld();
        // Private immutable archive plus per-aggregate migration entries make interrupted copies resumable.
        await maintenance.ArchiveSnapshotRestoreAsync(snapshot, linked.Token).ConfigureAwait(false);
        return await CopyAsync(snapshot, true, linked.Token).ConfigureAwait(false);
    }

    private async Task<OpportunityMigrationResult> CopyAsync(MaintenanceSnapshot snapshot, bool apply, CancellationToken ct)
    {
        var current = await maintenance.ReadAllAsync(ct).ConfigureAwait(false);
        foreach (var name in MaintenanceIntegrity.TableNames)
        {
            var before = snapshot.Tables[name].ToDictionary(e => (e.PartitionKey, e.RowKey));
            if (before.Count != current[name].Count || current[name].Any(e => !before.TryGetValue((e.PartitionKey, e.RowKey), out var expected) ||
                e.ETag != expected.ETag || MaintenanceIntegrity.EntityChecksum(e) != MaintenanceIntegrity.EntityChecksum(expected)))
                throw new InvalidOperationException("Source changed after snapshot. Drain writers and export again.");
        }
        var source = snapshot.Tables["Events"];
        if (source.Any(e => e.PartitionKey != "events" || !SlugSanitizer.IsValid(e.RowKey)))
            throw new InvalidOperationException("Unexpected legacy event keys require explicit review before migration.");
        if (source.Any(e => e.Properties.Keys.Any(IsReservedProperty)))
            throw new InvalidOperationException("Legacy properties collide with opportunity-owned metadata; review them before migration.");
        var sourceKeys = source.Select(e => e.RowKey).ToHashSet(StringComparer.Ordinal);
        if (current["Opportunities"].Any(e => (e.PartitionKey != "__migration" || e.RowKey != "ready") &&
            (!sourceKeys.Contains(e.PartitionKey) || e.RowKey is not ("current" or "history-migration"))))
            throw new InvalidOperationException("Destination contains unrelated writes; migration cannot overwrite them.");
        var destination = tables.GetTableClient(options.Value.OpportunitiesTableName);
        var existing = 0;
        var created = 0;
        foreach (var original in source.OrderBy(e => e.RowKey, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var checksum = MaintenanceIntegrity.EntityChecksum(original);
            var originalEntity = MaintenanceRawMapping.ToEntity(original);
            var record = OpportunityMapping.Import(Mapping.ToEventRecord(originalEntity));
            var row = OpportunityRepository.Write(originalEntity, record);
            row["MigrationSourceChecksum"] = checksum;
            row["MigrationSnapshotChecksum"] = snapshot.Checksum;
            var history = new OpportunityHistoryEntry
            {
                Id = "history-migration", Kind = "Migration", AtUtc = snapshot.CreatedUtc, Actor = "migration",
                EventSlug = record.Slug, EventName = record.Event.Name, EventSnapshot = record.Event,
                Status = record.Status, Verdict = record.SystemVerdict, Reason = record.Event.NextAction ?? "Preserved legacy placement; historical scores are unknown.",
            };
            var audit = new TableEntity(record.Slug, history.Id) { ["Kind"] = "History", ["IsFeedback"] = false };
            OpportunityRepository.WritePayload(audit, "Json", JsonSerializer.Serialize(history, OpportunityRepository.Json));
            var prior = await destination.GetEntityIfExistsAsync<TableEntity>(original.RowKey, "current", cancellationToken: ct).ConfigureAwait(false);
            if (prior.HasValue)
            {
                var priorAudit = await destination.GetEntityIfExistsAsync<TableEntity>(original.RowKey, history.Id, cancellationToken: ct).ConfigureAwait(false);
                if (LogicalChecksum(prior.Value!) != LogicalChecksum(row) || !priorAudit.HasValue || LogicalChecksum(priorAudit.Value!) != LogicalChecksum(audit))
                    throw new InvalidOperationException("Destination differs from this immutable migration snapshot.");
                existing++;
                continue;
            }
            if (!apply) continue;
            await destination.SubmitTransactionAsync([
                new TableTransactionAction(TableTransactionActionType.Add, row),
                new TableTransactionAction(TableTransactionActionType.Add, audit),
            ], ct).ConfigureAwait(false);
            created++;
        }
        if (apply)
        {
            var marker = new TableEntity("__migration", "ready") { ["SnapshotChecksum"] = snapshot.Checksum, ["SourceCount"] = source.Count };
            try { await destination.AddEntityAsync(marker, ct).ConfigureAwait(false); }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                var prior = (await destination.GetEntityAsync<TableEntity>("__migration", "ready", cancellationToken: ct).ConfigureAwait(false)).Value;
                if (prior.GetString("SnapshotChecksum") != snapshot.Checksum || prior.GetInt32("SourceCount") != source.Count)
                    throw new InvalidOperationException("Migration readiness belongs to another source snapshot.");
            }
        }
        return new(snapshot.Checksum, source.Count, existing, created, apply);
    }

    private static string LogicalChecksum(TableEntity row) => MaintenanceIntegrity.EntityChecksum(MaintenanceRawMapping.FromEntity(row));
    private static bool IsReservedProperty(string name) => name is "Kind" or "Status" or "ScoringInputRevision" or "MigrationSourceChecksum" or "MigrationSnapshotChecksum"
        || name == "OpportunityJson" || name.StartsWith("OpportunityJsonChunk", StringComparison.Ordinal);
}
