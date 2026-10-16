using Azure.Data.Tables;
using Azure.Storage.Blobs;
using SpeakerPipeline.Core;
using SpeakerPipeline.Storage;

namespace SpeakerPipeline.Storage.Tests;

public sealed class MaintenanceAzuriteTests
{
    [MaintenanceAzuriteFact]
    public async Task Raw_export_full_rehearsal_cleanup_and_rollback_use_real_sdk_conditionals()
    {
        var connection = Environment.GetEnvironmentVariable("PIPELINE_AZURITE_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Set PIPELINE_AZURITE_CONNECTION_STRING for this explicit local integration test.");
        var tables = new TableServiceClient(connection);
        var blobs = new BlobServiceClient(connection);
        Assert.True(tables.Uri.IsLoopback);
        Assert.True(blobs.Uri.IsLoopback);
        var suffix = Guid.NewGuid().ToString("n");
        var sourceContainer = blobs.GetBlobContainerClient("source" + suffix);
        var sourceTarget = Target(tables, "S" + suffix);
        var source = new AzureMaintenanceStore(tables, sourceContainer, sourceTarget);
        await source.InitializeLocalRestoreAsync(CancellationToken.None);

        async Task Add(string table, TableEntity entity) => await tables.GetTableClient(sourceTarget.TableNames[table]).AddEntityAsync(entity);
        await Add("Events", new("events", "fictional") { ["Name"] = "Fictional event" });
        await Add("Events", new("events", "real") { ["Name"] = "Retain actual history" });
        await Add("Talks", new("talks", "canonical") { ["CanonicalTitle"] = "Real talk", ["DeckUrl"] = "<placeholder-deck-url>", ["FutureLong"] = long.MaxValue });
        await Add("Submissions", new("fictional", "s") { ["TalkSlug"] = "canonical", ["AbstractUsed"] = "Fictional submission" });
        await Add("Blackouts", new("blackout", "family") { ["Reason"] = "Retain private availability", ["Hard"] = true });
        await Add("NotificationLog", new("2026-10", "real-notice") { ["DedupeKey"] = "retain-real-dedupe" });
        await Add("Topics", new("topic", "unknown-properties")
        {
            ["Binary"] = new byte[] { 0, 17, 255 }, ["Guid"] = Guid.NewGuid(), ["Int"] = int.MaxValue,
            ["Long"] = long.MaxValue, ["Double"] = 0.12345678912345678d,
            ["Date"] = DateTimeOffset.Parse("2026-10-16T12:13:14.1234567Z"), ["String"] = "unknown schema data", ["Boolean"] = true,
        });
        var service = new PipelineMaintenanceService(source);
        var snapshot = await service.ExportAsync(true);
        Assert.Equal(7, snapshot.Tables.Values.Sum(rows => rows.Count));
        Assert.Equal(8, snapshot.Tables["Topics"].Single().Properties.Count);

        // Preview accepts nonexistent emulator tables without creating them.
        var destinationTarget = Target(tables, "D" + suffix);
        var destinationContainer = blobs.GetBlobContainerClient("destination" + suffix);
        var destination = new AzureMaintenanceStore(tables, destinationContainer, destinationTarget);
        var rehearsal = new PipelineMaintenanceService(destination);
        var preview = await rehearsal.RestoreSnapshotAsync(new() { Snapshot = snapshot }, allowLocalRehearsal: true);
        Assert.Equal(0, preview.CompletedCount);
        Assert.False((await destinationContainer.ExistsAsync()).Value);
        var restored = await rehearsal.RestoreSnapshotAsync(new() { Snapshot = snapshot, Apply = true }, allowLocalRehearsal: true);
        Assert.True(restored.Restored);
        AssertLogicalEqual(snapshot, await rehearsal.ExportAsync(true));
        await rehearsal.RestoreSnapshotAsync(new() { Snapshot = snapshot, Apply = true }, allowLocalRehearsal: true);

        var plan = new MaintenancePlan
        {
            OperationId = "cleanup-" + suffix, Snapshot = snapshot,
            Changes =
            [
                Change(snapshot, "Events", "events", "fictional"),
                Change(snapshot, "Talks", "talks", "canonical", "DeckUrl"),
                Change(snapshot, "Submissions", "fictional", "s"),
                Change(snapshot, "Events", "events", "real") with
                {
                    Kind = MaintenanceChangeKind.SetProperties,
                    PropertiesToSet = new() { ["FamilyConflictFlag"] = new(MaintenanceValueType.Boolean, "true") },
                },
            ],
        };
        await service.CleanupAsync(new() { Plan = plan });
        Assert.False((await sourceContainer.GetBlobClient("maintenance/" + plan.OperationId + ".json").ExistsAsync()).Value);
        var applied = await service.CleanupAsync(new() { Plan = plan, Apply = true });
        Assert.Equal("Completed", applied.Status);
        var after = await service.ExportAsync(true);
        Assert.False(after.Tables["Talks"].Single().Properties.ContainsKey("DeckUrl"));
        Assert.Equal(long.MaxValue.ToString(), after.Tables["Talks"].Single().Properties["FutureLong"].Value);
        Assert.Single(after.Tables["Events"]);
        Assert.Equal(new(MaintenanceValueType.Boolean, "true"), after.Tables["Events"].Single().Properties["FamilyConflictFlag"]);
        Assert.Empty(after.Tables["Submissions"]);
        Assert.Equal("Completed", (await service.GetOperationAsync(plan.OperationId))!.Status);
        await service.CleanupAsync(new() { Plan = plan, Apply = true });
        await service.RestoreAsync(new() { OperationId = plan.OperationId });
        Assert.True((await service.RestoreAsync(new() { OperationId = plan.OperationId, Apply = true })).Restored);
        AssertLogicalEqual(snapshot, await service.ExportAsync(true));
        await service.RestoreAsync(new() { OperationId = plan.OperationId, Apply = true });
    }

    private static MaintenanceTarget Target(TableServiceClient tables, string prefix) => new()
    {
        TableEndpoint = tables.Uri.AbsoluteUri.TrimEnd('/'),
        TableNames = MaintenanceIntegrity.TableNames.ToDictionary(n => n, n => prefix + n),
    };
    private static MaintenanceChange Change(MaintenanceSnapshot snapshot, string table, string partition, string row, string? remove = null)
    {
        var entity = snapshot.Tables[table].Single(e => e.PartitionKey == partition && e.RowKey == row);
        return new()
        {
            Table = table, PartitionKey = partition, RowKey = row, ExpectedETag = entity.ETag,
            Kind = remove is null ? MaintenanceChangeKind.Delete : MaintenanceChangeKind.RemoveProperties,
            PropertiesToRemove = remove is null ? [] : [remove],
        };
    }
    private static void AssertLogicalEqual(MaintenanceSnapshot expected, MaintenanceSnapshot actual)
    {
        foreach (var table in MaintenanceIntegrity.TableNames)
            Assert.Equal(expected.Tables[table].Select(MaintenanceIntegrity.EntityChecksum).Order(), actual.Tables[table].Select(MaintenanceIntegrity.EntityChecksum).Order());
    }
}

public sealed class MaintenanceAzuriteFactAttribute : FactAttribute
{
    public MaintenanceAzuriteFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PIPELINE_AZURITE_MAINTENANCE_TEST") != "1")
            Skip = "Explicit local Azurite integration: set PIPELINE_AZURITE_MAINTENANCE_TEST=1 and PIPELINE_AZURITE_CONNECTION_STRING.";
    }
}
