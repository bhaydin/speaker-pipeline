using System.Security.Cryptography;
using System.Text.Json;

namespace SpeakerPipeline.Core;

public enum MaintenanceValueType { String, Boolean, Int32, Int64, Double, DateTime, Guid, Binary }
public enum MaintenanceChangeKind { Delete, RemoveProperties, SetProperties }

/// <summary>Lossless Table Storage property representation; Value uses invariant text or base64.</summary>
public sealed record MaintenanceValue(MaintenanceValueType Type, string Value);

public sealed record MaintenanceEntity
{
    public required string PartitionKey { get; init; }
    public required string RowKey { get; init; }
    public required string ETag { get; init; }
    public DateTimeOffset? Timestamp { get; init; }
    public Dictionary<string, MaintenanceValue> Properties { get; init; } = new(StringComparer.Ordinal);
}

public sealed record MaintenanceTarget
{
    public required string TableEndpoint { get; init; }
    public Dictionary<string, string> TableNames { get; init; } = new(StringComparer.Ordinal);
}

public sealed record MaintenanceSnapshot
{
    public int Version { get; init; } = 1;
    public required string SnapshotId { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public bool WritersFrozen { get; init; }
    public required MaintenanceTarget Target { get; init; }
    public Dictionary<string, List<MaintenanceEntity>> Tables { get; init; } = new(StringComparer.Ordinal);
    public string Checksum { get; init; } = "";
}

public sealed record MaintenanceChange
{
    public required string Table { get; init; }
    public required string PartitionKey { get; init; }
    public required string RowKey { get; init; }
    public required string ExpectedETag { get; init; }
    public MaintenanceChangeKind Kind { get; init; }
    public List<string> PropertiesToRemove { get; init; } = [];
    public Dictionary<string, MaintenanceValue> PropertiesToSet { get; init; } = new(StringComparer.Ordinal);
}

public sealed record MaintenancePlan
{
    public required string OperationId { get; init; }
    public required MaintenanceSnapshot Snapshot { get; init; }
    public List<MaintenanceChange> Changes { get; init; } = [];
    public string? Purpose { get; init; }
}

public sealed record MaintenanceApplyRequest
{
    public required MaintenancePlan Plan { get; init; }
    public bool Apply { get; init; }
}

public sealed record MaintenanceRestoreRequest
{
    public required string OperationId { get; init; }
    public bool Apply { get; init; }
}

public sealed record MaintenanceSnapshotRestoreRequest
{
    public required MaintenanceSnapshot Snapshot { get; init; }
    public bool Apply { get; init; }
}

public sealed record MaintenanceResult
{
    public required string OperationId { get; init; }
    public required string SnapshotChecksum { get; init; }
    public required string Status { get; init; }
    public int ChangeCount { get; init; }
    public int CompletedCount { get; init; }
    public bool Applied { get; init; }
    public bool Restored { get; init; }
}

/// <summary>API-owned maintenance boundary. No ordinary client or agent receives this capability.</summary>
public interface IPipelineMaintenanceService
{
    Task<MaintenanceSnapshot> ExportAsync(bool writersFrozen, CancellationToken ct = default);
    Task<MaintenanceResult> CleanupAsync(MaintenanceApplyRequest request, CancellationToken ct = default);
    Task<MaintenanceResult> RestoreAsync(MaintenanceRestoreRequest request, CancellationToken ct = default);
    Task<MaintenanceResult> RestoreSnapshotAsync(MaintenanceSnapshotRestoreRequest request, bool allowLocalRehearsal = false, CancellationToken ct = default);
    Task<MaintenanceResult?> GetOperationAsync(string operationId, CancellationToken ct = default);
}

/// <summary>Stable checksums independent of JSON property order. Azure ETags/timestamps are audit metadata.</summary>
public static class MaintenanceIntegrity
{
    public static readonly string[] TableNames = ["Events", "Submissions", "Talks", "Topics", "Blackouts", "NotificationLog"];

    public static string SnapshotChecksum(MaintenanceSnapshot snapshot) => Hash(new
    {
        snapshot.Version, snapshot.SnapshotId, CreatedUtc = snapshot.CreatedUtc.ToUniversalTime().ToString("O"),
        snapshot.WritersFrozen,
        Target = new { snapshot.Target.TableEndpoint, TableNames = snapshot.Target.TableNames.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray() },
        Tables = snapshot.Tables.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => new
        {
            Table = t.Key,
            Rows = t.Value.OrderBy(e => e.PartitionKey, StringComparer.Ordinal).ThenBy(e => e.RowKey, StringComparer.Ordinal)
                .Select(e => new { e.PartitionKey, e.RowKey, e.ETag, Timestamp = e.Timestamp?.ToUniversalTime().ToString("O"), Properties = Ordered(e.Properties) }).ToArray(),
        }).ToArray(),
    });

    public static string EntityChecksum(MaintenanceEntity entity) => Hash(new { entity.PartitionKey, entity.RowKey, Properties = Ordered(entity.Properties) });

    public static string PlanChecksum(MaintenancePlan plan) => Hash(new
    {
        plan.OperationId, SnapshotChecksum = SnapshotChecksum(plan.Snapshot), plan.Purpose,
        Changes = plan.Changes.OrderBy(c => c.Table, StringComparer.Ordinal).ThenBy(c => c.PartitionKey, StringComparer.Ordinal)
            .ThenBy(c => c.RowKey, StringComparer.Ordinal).Select(c => new
            {
                c.Table, c.PartitionKey, c.RowKey, c.ExpectedETag, c.Kind,
                PropertiesToRemove = c.PropertiesToRemove.Order(StringComparer.Ordinal).ToArray(),
                PropertiesToSet = Ordered(c.PropertiesToSet),
            }).ToArray(),
    });

    private static KeyValuePair<string, MaintenanceValue>[] Ordered(Dictionary<string, MaintenanceValue> properties)
        => properties.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
    private static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
