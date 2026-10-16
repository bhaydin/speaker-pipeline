namespace SpeakerPipeline.Core;

public sealed record OpportunityMigrationRequest
{
    public required MaintenanceSnapshot Snapshot { get; init; }
    public bool Apply { get; init; }
}
public sealed record OpportunityMigrationResult(string SnapshotChecksum, int SourceCount, int ExistingCount, int CreatedCount, bool Applied);
public interface IOpportunityMigrationService
{
    Task<OpportunityMigrationResult> MigrateAsync(OpportunityMigrationRequest request, bool writersFrozen, CancellationToken ct = default);
}
