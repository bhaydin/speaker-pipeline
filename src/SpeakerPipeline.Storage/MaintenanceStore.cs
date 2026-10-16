using System.Globalization;
using System.Diagnostics;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Storage;

internal sealed record MaintenanceJournal
{
    public required MaintenancePlan Plan { get; init; }
    public required string PlanChecksum { get; init; }
    public List<MaintenanceJournalItem> Items { get; init; } = [];
    public bool Restoring { get; set; }
    public bool Restored { get; set; }
}

internal sealed record MaintenanceJournalItem
{
    public required MaintenanceChange Change { get; init; }
    public string State { get; set; } = "Planned";
    public string? AfterETag { get; set; }
    public string? RestoredETag { get; set; }
}

internal interface IMaintenanceStore
{
    MaintenanceTarget Target { get; }
    Task<Dictionary<string, List<MaintenanceEntity>>> ReadAllAsync(CancellationToken ct, bool allowMissingTables = false);
    Task<MaintenanceEntity?> GetAsync(MaintenanceChange key, CancellationToken ct);
    Task DeleteAsync(MaintenanceChange change, string etag, CancellationToken ct);
    Task<string> ReplaceAsync(string table, MaintenanceEntity entity, string etag, CancellationToken ct);
    Task<string> AddAsync(string table, MaintenanceEntity entity, CancellationToken ct);
    Task<(MaintenanceJournal Journal, string ETag)?> ReadJournalAsync(string operationId, CancellationToken ct);
    Task<string> SaveJournalAsync(MaintenanceJournal journal, string? etag, CancellationToken ct);
    Task<IMaintenanceLock> AcquireLockAsync(CancellationToken ct);
    Task InitializeLocalRestoreAsync(CancellationToken ct);
    Task ArchiveSnapshotRestoreAsync(MaintenanceSnapshot snapshot, CancellationToken ct);
}

internal interface IMaintenanceLock : IAsyncDisposable
{
    CancellationToken Lost { get; }
    void EnsureHeld();
}

internal sealed class AzureMaintenanceStore(TableServiceClient tables, BlobContainerClient container, MaintenanceTarget target) : IMaintenanceStore
{
    private const string Prefix = "maintenance/";
    public MaintenanceTarget Target { get; } = target;

    public async Task<Dictionary<string, List<MaintenanceEntity>>> ReadAllAsync(CancellationToken ct, bool allowMissingTables = false)
    {
        var result = new Dictionary<string, List<MaintenanceEntity>>(StringComparer.Ordinal);
        foreach (var name in MaintenanceIntegrity.TableNames)
        {
            var rows = new List<MaintenanceEntity>();
            try
            {
                await foreach (var entity in Table(name).QueryAsync<TableEntity>(cancellationToken: ct).ConfigureAwait(false))
                    rows.Add(MaintenanceRawMapping.FromEntity(entity));
            }
            catch (RequestFailedException ex) when (ex.Status == 404 && allowMissingTables && tables.Uri.IsLoopback) { }
            result.Add(name, rows);
        }
        return result;
    }

    public async Task InitializeLocalRestoreAsync(CancellationToken ct)
    {
        if (!tables.Uri.IsLoopback || !container.Uri.IsLoopback)
            throw new InvalidOperationException("Only loopback emulator resources may be initialized by snapshot restore.");
        foreach (var name in MaintenanceIntegrity.TableNames)
            await Table(name).CreateIfNotExistsAsync(ct).ConfigureAwait(false);
        await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task ArchiveSnapshotRestoreAsync(MaintenanceSnapshot snapshot, CancellationToken ct)
    {
        await EnsurePrivateAsync(ct).ConfigureAwait(false);
        var blob = container.GetBlobClient(Prefix + "snapshot-restores/" + snapshot.Checksum + ".json");
        var archive = new SnapshotRestoreArchive(snapshot, Target);
        try
        {
            await blob.UploadAsync(BinaryData.FromObjectAsJson(archive), new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
            }, ct).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            var existing = (await blob.DownloadContentAsync(ct).ConfigureAwait(false)).Value.Content.ToObjectFromJson<SnapshotRestoreArchive>();
            if (existing is null || MaintenanceIntegrity.SnapshotChecksum(existing.Snapshot) != snapshot.Checksum ||
                existing.Destination.TableEndpoint != Target.TableEndpoint ||
                existing.Destination.TableNames.Count != Target.TableNames.Count ||
                Target.TableNames.Any(p => !existing.Destination.TableNames.TryGetValue(p.Key, out var name) || name != p.Value))
                throw new InvalidOperationException("Snapshot restore archive does not match this request and destination.");
        }
    }

    public async Task<MaintenanceEntity?> GetAsync(MaintenanceChange key, CancellationToken ct)
    {
        var response = await Table(key.Table).GetEntityIfExistsAsync<TableEntity>(key.PartitionKey, key.RowKey, cancellationToken: ct).ConfigureAwait(false);
        return response.HasValue ? MaintenanceRawMapping.FromEntity(response.Value!) : null;
    }

    public async Task DeleteAsync(MaintenanceChange change, string etag, CancellationToken ct)
        => await Table(change.Table).DeleteEntityAsync(change.PartitionKey, change.RowKey, new ETag(etag), ct).ConfigureAwait(false);

    public async Task<string> ReplaceAsync(string table, MaintenanceEntity entity, string etag, CancellationToken ct)
    {
        var response = await Table(table).UpdateEntityAsync(MaintenanceRawMapping.ToEntity(entity), new ETag(etag), TableUpdateMode.Replace, ct).ConfigureAwait(false);
        return response.Headers.ETag?.ToString() ?? throw new InvalidOperationException("Storage did not return the replacement ETag.");
    }

    public async Task<string> AddAsync(string table, MaintenanceEntity entity, CancellationToken ct)
    {
        var response = await Table(table).AddEntityAsync(MaintenanceRawMapping.ToEntity(entity), ct).ConfigureAwait(false);
        return response.Headers.ETag?.ToString() ?? throw new InvalidOperationException("Storage did not return the inserted ETag.");
    }

    public async Task<(MaintenanceJournal Journal, string ETag)?> ReadJournalAsync(string operationId, CancellationToken ct)
    {
        try
        {
            var result = await JournalBlob(operationId).DownloadContentAsync(ct).ConfigureAwait(false);
            return (result.Value.Content.ToObjectFromJson<MaintenanceJournal>() ?? throw new InvalidOperationException("Invalid maintenance journal."), result.Value.Details.ETag.ToString());
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    public async Task<string> SaveJournalAsync(MaintenanceJournal journal, string? etag, CancellationToken ct)
    {
        await EnsurePrivateAsync(ct).ConfigureAwait(false);
        var response = await JournalBlob(journal.Plan.OperationId).UploadAsync(BinaryData.FromObjectAsJson(journal), new BlobUploadOptions
        {
            Conditions = etag is null ? new BlobRequestConditions { IfNoneMatch = ETag.All } : new BlobRequestConditions { IfMatch = new ETag(etag) },
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
        }, ct).ConfigureAwait(false);
        return response.Value.ETag.ToString();
    }

    public async Task<IMaintenanceLock> AcquireLockAsync(CancellationToken ct)
    {
        await EnsurePrivateAsync(ct).ConfigureAwait(false);
        var blob = container.GetBlobClient(Prefix + "lock");
        try
        {
            await blob.UploadAsync(BinaryData.FromString("Pipeline maintenance lock"), new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
            }, ct).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412) { }
        // A finite lease survives neither an abandoned process nor a permanently held lock.
        var lease = blob.GetBlobLeaseClient();
        var started = Stopwatch.GetTimestamp();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await lease.AcquireAsync(TimeSpan.FromSeconds(60), cancellationToken: deadline.Token).ConfigureAwait(false);
        return new RenewableMaintenanceLease(lease, started);
    }

    private async Task EnsurePrivateAsync(CancellationToken ct)
    {
        var properties = await container.GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false);
        if (properties.Value.PublicAccess != PublicAccessType.None)
            throw new InvalidOperationException("Maintenance journals require an existing private blob container.");
    }
    private TableClient Table(string logical) => tables.GetTableClient(Target.TableNames[logical]);
    private BlobClient JournalBlob(string operationId) => container.GetBlobClient(Prefix + operationId + ".json");
    private sealed record SnapshotRestoreArchive(MaintenanceSnapshot Snapshot, MaintenanceTarget Destination);
}

internal sealed class RenewableMaintenanceLease : IMaintenanceLock
{
    private readonly BlobLeaseClient _lease;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _lost = new();
    private readonly Task _renew;
    private long _confirmedAt;
    public CancellationToken Lost => _lost.Token;

    public RenewableMaintenanceLease(BlobLeaseClient lease, long confirmedAt)
    {
        _lease = lease;
        _confirmedAt = confirmedAt;
        _renew = RenewAsync();
    }

    public void EnsureHeld()
    {
        // Measure from the request's START, not its response, so network delay cannot
        // make an expired server lease appear valid. Leave 15 seconds for in-flight IO.
        if (Stopwatch.GetElapsedTime(Volatile.Read(ref _confirmedAt)) >= TimeSpan.FromSeconds(45))
            _lost.Cancel();
        _lost.Token.ThrowIfCancellationRequested();
    }
    private async Task RenewAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                EnsureHeld();
                var started = Stopwatch.GetTimestamp();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                await _lease.RenewAsync(cancellationToken: deadline.Token).ConfigureAwait(false);
                Volatile.Write(ref _confirmedAt, started);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch { await _lost.CancelAsync().ConfigureAwait(false); }
    }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _renew.ConfigureAwait(false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await _lease.ReleaseAsync(cancellationToken: deadline.Token).ConfigureAwait(false); }
        catch (RequestFailedException) { } // A lost connection is bounded by the finite lease.
        catch (OperationCanceledException) { }
        _stop.Dispose();
        _lost.Dispose();
    }
}

internal static class MaintenanceRawMapping
{
    public static MaintenanceEntity FromEntity(TableEntity entity) => new()
    {
        PartitionKey = entity.PartitionKey, RowKey = entity.RowKey, ETag = entity.ETag.ToString(), Timestamp = entity.Timestamp,
        Properties = entity.Where(p => p.Key is not ("PartitionKey" or "RowKey" or "Timestamp" or "odata.etag") && p.Value is not null)
            .ToDictionary(p => p.Key, p => Encode(p.Value), StringComparer.Ordinal),
    };

    public static TableEntity ToEntity(MaintenanceEntity entity)
    {
        var result = new TableEntity(entity.PartitionKey, entity.RowKey);
        foreach (var (name, value) in entity.Properties)
        {
            if (name is "PartitionKey" or "RowKey" or "Timestamp" or "odata.etag")
                throw new ArgumentException("System properties cannot appear in the property payload.");
            result[name] = Decode(value);
        }
        return result;
    }

    internal static MaintenanceValue Encode(object value) => value switch
    {
        string s => new(MaintenanceValueType.String, s),
        bool b => new(MaintenanceValueType.Boolean, b ? "true" : "false"),
        int i => new(MaintenanceValueType.Int32, i.ToString(CultureInfo.InvariantCulture)),
        long l => new(MaintenanceValueType.Int64, l.ToString(CultureInfo.InvariantCulture)),
        double d => new(MaintenanceValueType.Double, d.ToString("R", CultureInfo.InvariantCulture)),
        DateTimeOffset d => new(MaintenanceValueType.DateTime, d.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
        DateTime d => new(MaintenanceValueType.DateTime, new DateTimeOffset(d.ToUniversalTime()).ToString("O", CultureInfo.InvariantCulture)),
        Guid g => new(MaintenanceValueType.Guid, g.ToString("D")),
        byte[] b => new(MaintenanceValueType.Binary, Convert.ToBase64String(b)),
        BinaryData b => new(MaintenanceValueType.Binary, Convert.ToBase64String(b.ToArray())),
        _ => throw new InvalidOperationException($"Unsupported Table Storage property type: {value.GetType().Name}."),
    };

    internal static object Decode(MaintenanceValue value) => value.Type switch
    {
        MaintenanceValueType.String => value.Value,
        MaintenanceValueType.Boolean => bool.Parse(value.Value),
        MaintenanceValueType.Int32 => int.Parse(value.Value, CultureInfo.InvariantCulture),
        MaintenanceValueType.Int64 => long.Parse(value.Value, CultureInfo.InvariantCulture),
        MaintenanceValueType.Double => double.Parse(value.Value, CultureInfo.InvariantCulture),
        MaintenanceValueType.DateTime => DateTimeOffset.Parse(value.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        MaintenanceValueType.Guid => Guid.Parse(value.Value),
        MaintenanceValueType.Binary => Convert.FromBase64String(value.Value),
        _ => throw new ArgumentException("Unknown Table Storage property type."),
    };
}
