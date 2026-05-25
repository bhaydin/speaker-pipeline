using System.Runtime.CompilerServices;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Storage;

internal sealed class EventRepository(TableClient client, ILogger<EventRepository> logger) : IEventRepository
{
    public async Task<EventRecord?> GetAsync(string slug, CancellationToken ct = default)
    {
        try
        {
            var response = await client.GetEntityAsync<TableEntity>(Mapping.EventsPartitionKey, slug, cancellationToken: ct).ConfigureAwait(false);
            return Mapping.ToEventRecord(response.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async IAsyncEnumerable<EventRecord> QueryAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var query = client.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{Mapping.EventsPartitionKey}'",
            cancellationToken: ct);

        await foreach (var entity in query.ConfigureAwait(false))
        {
            yield return Mapping.ToEventRecord(entity);
        }
    }

    public async Task<IReadOnlyList<EventRecord>> GetByCategoryAsync(IReadOnlyList<EventCategory> categories, CancellationToken ct = default)
    {
        if (categories.Count == 0)
        {
            return [];
        }

        var categoryFilter = string.Join(" or ", categories.Select(c => $"Category eq '{c}'"));
        var filter = $"PartitionKey eq '{Mapping.EventsPartitionKey}' and ({categoryFilter})";

        return await CollectAsync(filter, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<EventRecord>> GetUpcomingDeadlinesAsync(TimeSpan window, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.Add(window);
        var filter =
            $"PartitionKey eq '{Mapping.EventsPartitionKey}' " +
            $"and CfpDeadline lt datetime'{Mapping.FormatOdataDateTime(cutoff)}' " +
            $"and CfpDeadline gt datetime'{Mapping.FormatOdataDateTime(DateTimeOffset.UtcNow)}'";

        return await CollectAsync(filter, ct).ConfigureAwait(false);
    }

    public async Task UpsertAsync(EventRecord record, CancellationToken ct = default)
    {
        var entity = Mapping.ToEntity(record);
        await client.UpsertEntityAsync(entity, TableUpdateMode.Merge, ct).ConfigureAwait(false);
        logger.LogInformation("Upserted event {Slug}", record.Slug);
    }

    public async Task DeleteAsync(string slug, CancellationToken ct = default)
    {
        await client.DeleteEntityAsync(Mapping.EventsPartitionKey, slug, ETag.All, ct).ConfigureAwait(false);
        logger.LogInformation("Deleted event {Slug}", slug);
    }

    private async Task<IReadOnlyList<EventRecord>> CollectAsync(string filter, CancellationToken ct)
    {
        var list = new List<EventRecord>();
        var query = client.QueryAsync<TableEntity>(filter: filter, cancellationToken: ct);
        await foreach (var entity in query.ConfigureAwait(false))
        {
            list.Add(Mapping.ToEventRecord(entity));
        }
        return list;
    }
}
