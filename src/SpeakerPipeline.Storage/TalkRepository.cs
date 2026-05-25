using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Storage;

internal sealed class TalkRepository(TableClient client, ILogger<TalkRepository> logger) : ITalkRepository
{
    public async Task<TalkRecord?> GetAsync(string slug, CancellationToken ct = default)
    {
        try
        {
            var response = await client.GetEntityAsync<TableEntity>(Mapping.TalksPartitionKey, slug, cancellationToken: ct).ConfigureAwait(false);
            return Mapping.ToTalkRecord(response.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<TalkRecord>> GetAllAsync(CancellationToken ct = default)
    {
        return await CollectAsync($"PartitionKey eq '{Mapping.TalksPartitionKey}'", ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TalkRecord>> GetByLaneAsync(Lane lane, CancellationToken ct = default)
    {
        var filter = $"PartitionKey eq '{Mapping.TalksPartitionKey}' and Lane eq '{lane}'";
        return await CollectAsync(filter, ct).ConfigureAwait(false);
    }

    public async Task UpsertAsync(TalkRecord record, CancellationToken ct = default)
    {
        var entity = Mapping.ToEntity(record);
        await client.UpsertEntityAsync(entity, TableUpdateMode.Merge, ct).ConfigureAwait(false);
        logger.LogInformation("Upserted talk {Slug}", record.Slug);
    }

    private async Task<IReadOnlyList<TalkRecord>> CollectAsync(string filter, CancellationToken ct)
    {
        var list = new List<TalkRecord>();
        var query = client.QueryAsync<TableEntity>(filter: filter, cancellationToken: ct);
        await foreach (var entity in query.ConfigureAwait(false))
        {
            list.Add(Mapping.ToTalkRecord(entity));
        }
        return list;
    }
}
