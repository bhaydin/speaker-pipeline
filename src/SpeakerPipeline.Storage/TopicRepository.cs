using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Storage;

internal sealed class TopicRepository(TableClient client, ILogger<TopicRepository> logger) : ITopicRepository
{
    public async Task<TopicRecord?> GetAsync(string topicId, CancellationToken ct = default)
    {
        try
        {
            var response = await client.GetEntityAsync<TableEntity>(Mapping.TopicsPartitionKey, topicId, cancellationToken: ct).ConfigureAwait(false);
            return Mapping.ToTopicRecord(response.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<TopicRecord>> GetAllAsync(CancellationToken ct = default)
        => await CollectAsync($"PartitionKey eq '{Mapping.TopicsPartitionKey}'", ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<TopicRecord>> GetByStageAsync(TopicStage stage, CancellationToken ct = default)
        => await CollectAsync($"PartitionKey eq '{Mapping.TopicsPartitionKey}' and Stage eq '{stage}'", ct).ConfigureAwait(false);

    public async Task UpsertAsync(TopicRecord record, CancellationToken ct = default)
    {
        var entity = Mapping.ToEntity(record);
        await client.UpsertEntityAsync(entity, TableUpdateMode.Merge, ct).ConfigureAwait(false);
        logger.LogInformation("Upserted topic {TopicId}", record.TopicId);
    }

    private async Task<IReadOnlyList<TopicRecord>> CollectAsync(string filter, CancellationToken ct)
    {
        var list = new List<TopicRecord>();
        var query = client.QueryAsync<TableEntity>(filter: filter, cancellationToken: ct);
        await foreach (var entity in query.ConfigureAwait(false))
        {
            list.Add(Mapping.ToTopicRecord(entity));
        }
        return list;
    }
}
