using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Notifications;

public static class OperationState
{
    public static string Key(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static OperationDocument Document<T>(string kind, string id, T value, DateTimeOffset now)
        => new(kind, id, JsonSerializer.Serialize(value), now);
    public static T? Read<T>(VersionedOperation? document)
        => document is null ? default : JsonSerializer.Deserialize<T>(document.Document.Payload);
}

public sealed record DeliveryState(string Status, string Owner, DateTimeOffset StartedUtc,
    IReadOnlyList<string>? MessageIds = null);

public sealed class DeliveryCoordinator(IOperationsApiClient operations, TimeProvider time)
{
    public async Task<NotificationDelivery> DeliverAsync(string key, Func<Task<NotificationDelivery>> send, CancellationToken ct)
    {
        var id = OperationState.Key(key);
        var current = await operations.GetAsync("deliveries", id, ct);
        var prior = OperationState.Read<DeliveryState>(current);
        if (prior?.Status is "Sent" or "Uncertain") return new(prior.Status, prior.MessageIds);
        if (prior?.Status == "Sending")
            return new(time.GetUtcNow() - prior.StartedUtc < TimeSpan.FromMinutes(3) ? "Busy" : "Uncertain");
        var claim = new DeliveryState("Sending", Guid.NewGuid().ToString("N"), time.GetUtcNow());
        var claimed = await operations.SaveAsync(OperationState.Document("deliveries", id, claim, time.GetUtcNow()), current?.Version, ct);
        if (claimed is null) return new("Busy");
        NotificationDelivery result;
        try { result = await send(); }
        catch (OperationCanceledException) { result = new("Uncertain"); }
        catch (HttpRequestException ex) { result = new(ex.StatusCode is null ? "Uncertain" : "Failed"); }
        catch { result = new("Failed"); }
        // A failed receipt write intentionally leaves Sending; never blindly resend an ambiguous delivery.
        await operations.SaveAsync(OperationState.Document("deliveries", id,
            claim with { Status = result.Status, MessageIds = result.MessageIds }, time.GetUtcNow()), claimed.Version, CancellationToken.None);
        return result;
    }
}
