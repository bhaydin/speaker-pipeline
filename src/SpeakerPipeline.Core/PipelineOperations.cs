namespace SpeakerPipeline.Core;

/// <summary>API-owned operational state. Consumers never access its table directly.</summary>
public sealed record OperationDocument(string Kind, string Id, string Payload, DateTimeOffset UpdatedUtc);
public sealed record VersionedOperation(OperationDocument Document, string Version);

public interface IOperationsApiClient
{
    Task<VersionedOperation?> GetAsync(string kind, string id, CancellationToken ct = default);
    Task<VersionedOperation?> SaveAsync(OperationDocument document, string? expectedVersion = null, CancellationToken ct = default);
    Task<IReadOnlyList<VersionedOperation>> ListAsync(string kind, DateTimeOffset since, CancellationToken ct = default);
}

public interface IOperationsRepository : IOperationsApiClient;

public sealed record RunIssue(string Code, string Source, int Count = 1);
public sealed record AgentRunRecord
{
    public required string RunId { get; init; }
    public required string Agent { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; init; }
    public string Status { get; init; } = "Running";
    public int Considered { get; init; }
    public int Changed { get; init; }
    public IReadOnlyList<RunIssue> Issues { get; init; } = [];
    public IReadOnlyDictionary<string, long> Metrics { get; init; } = new Dictionary<string, long>();
    public decimal? EstimatedCost { get; init; }
    public string? ModelDeployment { get; init; }
}
