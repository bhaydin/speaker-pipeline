using System.Text.Json;
using Microsoft.Extensions.Logging;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Notifications;

public sealed record OutboxEntry(string DeliveryKey, NotificationChannel Channel, IReadOnlyList<string> Chunks,
    DateTimeOffset CreatedUtc, DateTimeOffset NextAttemptUtc, string Status = "Ready", int Attempts = 0);
public sealed record OutboxChunk(string Text);

/// <summary>API-backed exact original parts. Only confirmed failures are retryable.</summary>
public sealed class NotificationOutbox(IOperationsApiClient operations, DeliveryCoordinator coordinator, TimeProvider time,
    ILogger<NotificationOutbox>? logger = null)
{
    private const int ChunkLength = 8000;
    private const int MaxChunks = 256;
    private const int MaxAttempts = 5;

    public async Task<NotificationDelivery> DeliverAsync(string key, INotificationLane lane, NotificationPart part, CancellationToken ct)
    {
        var id = OperationState.Key(key);
        var document = await operations.GetAsync("outbox", id, ct);
        if (document is null)
        {
            // A rendered Telegram part needs only its own text/items and identity;
            // retaining every sibling group would multiply payload size needlessly.
            var stored = part.Text is null ? part : part with { Notification = part.Notification with { HtmlBody = "", Groups = [] } };
            var json = JsonSerializer.Serialize(stored);
            if (json.Length > ChunkLength * MaxChunks) throw new InvalidOperationException("Notification part exceeds the bounded outbox size.");
            var chunks = new List<string>();
            for (var offset = 0; offset < json.Length; offset += ChunkLength)
            {
                var text = json.Substring(offset, Math.Min(ChunkLength, json.Length - offset));
                var chunkId = OperationState.Key(id + ":" + offset + ":" + text);
                await operations.SaveAsync(OperationState.Document("outboxchunks", chunkId, new OutboxChunk(text), time.GetUtcNow()), ct: ct);
                chunks.Add(chunkId);
            }
            var entry = new OutboxEntry(key, lane.Channel, chunks, time.GetUtcNow(), time.GetUtcNow());
            document = await operations.SaveAsync(OperationState.Document("outbox", id, entry, time.GetUtcNow()), ct: ct)
                ?? await operations.GetAsync("outbox", id, ct)
                ?? throw new InvalidOperationException("Outbox identity could not be persisted.");
        }
        return await AttemptAsync(document, lane, ct);
    }

    public async Task<int> RetryAsync(IEnumerable<INotificationLane> lanes, CancellationToken ct)
    {
        var available = lanes.Where(l => l.IsEnabled).ToDictionary(l => l.Channel);
        var pending = (await operations.ListAsync("outbox", time.GetUtcNow().AddDays(-14), ct))
            .Where(d => OperationState.Read<OutboxEntry>(d) is { } e
                && e.NextAttemptUtc <= time.GetUtcNow() && e.Status is "Ready" or "Processing"
                && (available.ContainsKey(e.Channel) || e.CreatedUtc < time.GetUtcNow().AddDays(-7)))
            .OrderBy(d => d.Document.UpdatedUtc).Take(20).ToArray();
        var attempted = 0;
        foreach (var document in pending)
        {
            var entry = OperationState.Read<OutboxEntry>(document)!;
            if (entry.CreatedUtc < time.GetUtcNow().AddDays(-7))
            {
                await operations.SaveAsync(OperationState.Document("outbox", document.Document.Id,
                    entry with { Status = "Exhausted" }, time.GetUtcNow()), document.Version, ct);
                continue;
            }
            if (!available.TryGetValue(entry.Channel, out var lane)) continue;
            var claimed = await operations.SaveAsync(OperationState.Document("outbox", document.Document.Id,
                entry with { Status = "Processing", NextAttemptUtc = time.GetUtcNow().AddMinutes(3) }, time.GetUtcNow()), document.Version, ct);
            if (claimed is null) continue;
            try { await AttemptAsync(claimed, lane, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A receipt write can fail after a successful external send. Preserve
                // the lease/receipt and let the next pass classify it conservatively.
                logger?.LogError("Notification retry could not be completed ({Type}).", ex.GetType().Name);
            }
            attempted++;
        }
        return attempted;
    }

    private async Task<NotificationDelivery> AttemptAsync(VersionedOperation document, INotificationLane lane, CancellationToken ct)
    {
        var entry = OperationState.Read<OutboxEntry>(document)!;
        if (entry.Status == "Frozen") return new("Uncertain");
        if (entry.Status == "Exhausted") return new("Failed");
        if (!lane.IsEnabled) return new("Busy");
        var json = new System.Text.StringBuilder();
        foreach (var id in entry.Chunks)
        {
            var chunk = OperationState.Read<OutboxChunk>(await operations.GetAsync("outboxchunks", id, ct));
            if (chunk is null) throw new InvalidOperationException("Outbox payload is incomplete.");
            json.Append(chunk.Text);
        }
        var part = JsonSerializer.Deserialize<NotificationPart>(json.ToString()) ?? throw new InvalidOperationException("Outbox payload is invalid.");
        var result = await coordinator.DeliverAsync(entry.DeliveryKey, () => lane.SendPartAsync(part, ct), ct);
        var repairPending = false;
        if (result.Status == "Sent")
        {
            try { await lane.ReconcileReceiptAsync(part, result, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { repairPending = true; }
        }
        var attempts = entry.Attempts + (result.Status == "Busy" ? 0 : 1);
        var status = result.Status switch
        {
            "Sent" when !repairPending => "Completed",
            "Uncertain" => "Frozen",
            _ when attempts >= MaxAttempts => "Exhausted",
            _ => "Ready",
        };
        await operations.SaveAsync(OperationState.Document("outbox", document.Document.Id,
            entry with { Status = status, Attempts = attempts, NextAttemptUtc = time.GetUtcNow().AddMinutes(Math.Min(60, 5 * Math.Pow(2, attempts))) },
            time.GetUtcNow()), document.Version, CancellationToken.None);
        return result;
    }
}
