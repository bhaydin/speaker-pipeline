using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Storage;

internal sealed class CatalogRepository(TableClient table) : ICatalogRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public async Task<CatalogSnapshot> GetAsync(CancellationToken ct = default)
    {
        var pointer = await table.GetEntityIfExistsAsync<TableEntity>("control", "current", cancellationToken: ct);
        if (!pointer.HasValue) return new CatalogSnapshot();
        return await ReadGenerationAsync(pointer.Value!, ct);
    }

    public async Task<CatalogSnapshot?> GetGenerationAsync(string generationId, CancellationToken ct = default)
    {
        if (!Guid.TryParseExact(generationId, "N", out _)) return null;
        var published = await table.GetEntityIfExistsAsync<TableEntity>("control", "published-" + generationId, cancellationToken: ct);
        return published.HasValue ? await ReadGenerationAsync(published.Value!, ct) : null;
    }

    private async Task<CatalogSnapshot> ReadGenerationAsync(TableEntity manifest, CancellationToken ct)
    {
        var generation = manifest.GetString("GenerationId")!;
        var parts = new SortedDictionary<string, string>(StringComparer.Ordinal);
        await foreach (var entity in table.QueryAsync<TableEntity>($"PartitionKey eq '{generation}'", cancellationToken: ct))
            parts.Add(entity.RowKey, entity.GetString("Json")!);
        var json = string.Concat(parts.Values);
        if (Hash(json) != manifest.GetString("ContentHash"))
            throw new InvalidOperationException("Catalog generation is incomplete or corrupted.");
        return JsonSerializer.Deserialize<CatalogSnapshot>(json, Json)!;
    }

    public async Task PublishAsync(CatalogPublishRequest request, CancellationToken ct = default)
    {
        var snapshot = request.Snapshot;
        if (snapshot.Talks.Count == 0 || !Guid.TryParseExact(snapshot.GenerationId, "N", out _) ||
            snapshot.Talks.Select(t => t.TalkId).Distinct(StringComparer.Ordinal).Count() != snapshot.Talks.Count ||
            snapshot.Talks.Any(t => !snapshot.Versions.Any(v => v.TalkId == t.TalkId && v.VersionId == t.CurrentVersionId)))
            throw new ArgumentException("Catalog must contain unique talks with valid current versions and a generation ID.");
        var pointer = await table.GetEntityIfExistsAsync<TableEntity>("control", "current", cancellationToken: ct);
        var current = pointer.HasValue ? pointer.Value!.GetString("GenerationId") : null;
        if (!string.Equals(current ?? "", request.ExpectedGenerationId ?? "", StringComparison.Ordinal))
            throw new RequestFailedException(412, "Catalog changed; rebuild from the current generation.");
        var json = JsonSerializer.Serialize(snapshot, Json);
        // Immutable generation rows first, then one conditional pointer write. Readers never observe partial imports.
        for (var offset = 0; offset < json.Length; offset += 24000)
        {
            var part = new TableEntity(snapshot.GenerationId, (offset / 24000).ToString("D6"))
                { ["Json"] = json.Substring(offset, Math.Min(24000, json.Length - offset)) };
            await table.AddEntityAsync(part, ct);
        }
        var next = new TableEntity("control", "current") { ["GenerationId"] = snapshot.GenerationId, ["ContentHash"] = Hash(json) };
        var published = new TableEntity("control", "published-" + snapshot.GenerationId)
            { ["GenerationId"] = snapshot.GenerationId, ["ContentHash"] = Hash(json) };
        // Pointer and publication evidence share a partition and commit atomically.
        await table.SubmitTransactionAsync([
            pointer.HasValue ? new TableTransactionAction(TableTransactionActionType.UpdateReplace, next, pointer.Value!.ETag)
                : new TableTransactionAction(TableTransactionActionType.Add, next),
            new TableTransactionAction(TableTransactionActionType.Add, published)
        ], ct);
    }

    public async Task<bool> EnqueueAsync(CatalogSyncRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.DeliveryId) || request.DeliveryId.Length > 200)
            throw new ArgumentException("A bounded delivery identifier is required.");
        try
        {
            await table.AddEntityAsync(new TableEntity("jobs", Hash(request.DeliveryId))
            { ["Reason"] = request.Reason.Length > 100 ? request.Reason[..100] : request.Reason, ["State"] = "pending", ["QueuedAtUtc"] = DateTimeOffset.UtcNow }, ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 409) { return false; }
    }

    public async Task<CatalogSyncJob?> ClaimAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await foreach (var entity in table.QueryAsync<TableEntity>("PartitionKey eq 'jobs'", cancellationToken: ct))
        {
            var state = entity.GetString("State");
            if (state == "complete" || state == "failed" || entity.GetDateTimeOffset("LeaseExpiresUtc") > now) continue;
            var token = Guid.NewGuid().ToString("N");
            entity["LeaseToken"] = token;
            entity["LeaseExpiresUtc"] = now.AddMinutes(15);
            entity["State"] = "running";
            entity["Attempts"] = (entity.GetInt32("Attempts") ?? 0) + 1;
            try { await table.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace, ct); }
            catch (RequestFailedException ex) when (ex.Status == 412) { continue; }
            return new CatalogSyncJob(entity.RowKey, entity.GetString("Reason") ?? "sync", token, now.AddMinutes(15));
        }
        return null;
    }

    public async Task CompleteAsync(string id, CatalogJobCompletion request, CancellationToken ct = default)
    {
        var entity = (await table.GetEntityAsync<TableEntity>("jobs", id, cancellationToken: ct)).Value;
        if (entity.GetString("LeaseToken") != request.LeaseToken || entity.GetDateTimeOffset("LeaseExpiresUtc") < DateTimeOffset.UtcNow)
            throw new RequestFailedException(412, "Catalog job lease expired or changed.");
        entity["State"] = request.Succeeded ? "complete" : entity.GetInt32("Attempts") >= 3 ? "failed" : "pending";
        entity["ErrorCode"] = request.ErrorCode;
        entity["LeaseExpiresUtc"] = DateTimeOffset.UtcNow.AddMinutes(request.Succeeded ? 0 : 5);
        await table.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace, ct);
    }

    public async Task<CatalogExtraction?> GetExtractionAsync(string hash, CancellationToken ct = default)
    {
        var row = await table.GetEntityIfExistsAsync<TableEntity>("extractions", Hash(hash), cancellationToken: ct);
        return row.HasValue ? JsonSerializer.Deserialize<CatalogExtraction>(row.Value!.GetString("Json")!, Json) : null;
    }

    public Task SaveExtractionAsync(CatalogExtraction extraction, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(extraction, Json);
        if (json.Length > 24000) throw new ArgumentException("Extraction exceeds the bounded metadata limit.");
        return table.UpsertEntityAsync(new TableEntity("extractions", Hash(extraction.ContentHash)) { ["Json"] = json }, TableUpdateMode.Replace, ct);
    }
}
