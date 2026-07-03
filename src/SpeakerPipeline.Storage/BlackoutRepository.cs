using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Storage;

internal sealed class BlackoutRepository(TableClient client, ILogger<BlackoutRepository> logger) : IBlackoutRepository
{
    public async Task<BlackoutRecord?> GetAsync(string blackoutId, CancellationToken ct = default)
    {
        try
        {
            var response = await client.GetEntityAsync<TableEntity>(Mapping.BlackoutsPartitionKey, blackoutId, cancellationToken: ct).ConfigureAwait(false);
            return Mapping.ToBlackoutRecord(response.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<BlackoutRecord>> GetAllAsync(CancellationToken ct = default)
    {
        var list = new List<BlackoutRecord>();
        var query = client.QueryAsync<TableEntity>(filter: $"PartitionKey eq '{Mapping.BlackoutsPartitionKey}'", cancellationToken: ct);
        await foreach (var entity in query.ConfigureAwait(false))
        {
            list.Add(Mapping.ToBlackoutRecord(entity));
        }
        return list;
    }

    public async Task UpsertAsync(BlackoutRecord record, CancellationToken ct = default)
    {
        var entity = Mapping.ToEntity(record);
        await client.UpsertEntityAsync(entity, TableUpdateMode.Merge, ct).ConfigureAwait(false);
        logger.LogInformation("Upserted blackout {BlackoutId}", record.BlackoutId);
    }
}
