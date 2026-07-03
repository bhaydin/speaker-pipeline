using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Storage;

/// <summary>
/// Persistence for the notification dedupe ledger. Present from Milestone 1 so
/// the schema is complete; its first writer (the Notifier) lands in Milestone 2.
/// </summary>
internal sealed class NotificationLogRepository(TableClient client, ILogger<NotificationLogRepository> logger) : INotificationLogRepository
{
    public async Task<NotificationLogRecord?> GetAsync(string period, string notificationId, CancellationToken ct = default)
    {
        try
        {
            var response = await client.GetEntityAsync<TableEntity>(period, notificationId, cancellationToken: ct).ConfigureAwait(false);
            return Mapping.ToNotificationLogRecord(response.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<NotificationLogRecord>> GetForPeriodAsync(string period, CancellationToken ct = default)
    {
        var list = new List<NotificationLogRecord>();
        var query = client.QueryAsync<TableEntity>(filter: $"PartitionKey eq '{period}'", cancellationToken: ct);
        await foreach (var entity in query.ConfigureAwait(false))
        {
            list.Add(Mapping.ToNotificationLogRecord(entity));
        }
        return list;
    }

    public async Task UpsertAsync(NotificationLogRecord record, CancellationToken ct = default)
    {
        var entity = Mapping.ToEntity(record);
        await client.UpsertEntityAsync(entity, TableUpdateMode.Merge, ct).ConfigureAwait(false);
        logger.LogInformation("Upserted notification {Period}/{NotificationId}", record.Period, record.NotificationId);
    }
}
