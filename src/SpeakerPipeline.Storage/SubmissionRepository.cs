using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Storage;

internal sealed class SubmissionRepository(TableClient client, ILogger<SubmissionRepository> logger) : ISubmissionRepository
{
    public async Task<SubmissionRecord?> GetAsync(string eventSlug, string submissionId, CancellationToken ct = default)
    {
        try
        {
            var response = await client.GetEntityAsync<TableEntity>(eventSlug, submissionId, cancellationToken: ct).ConfigureAwait(false);
            return Mapping.ToSubmissionRecord(response.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<SubmissionRecord>> GetForEventAsync(string eventSlug, CancellationToken ct = default)
    {
        var list = new List<SubmissionRecord>();
        var query = client.QueryAsync<TableEntity>(filter: $"PartitionKey eq '{eventSlug}'", cancellationToken: ct);
        await foreach (var entity in query.ConfigureAwait(false))
        {
            list.Add(Mapping.ToSubmissionRecord(entity));
        }
        return list;
    }

    public async Task UpsertAsync(SubmissionRecord record, CancellationToken ct = default)
    {
        var entity = Mapping.ToEntity(record);
        await client.UpsertEntityAsync(entity, TableUpdateMode.Merge, ct).ConfigureAwait(false);
        logger.LogInformation("Upserted submission {EventSlug}/{SubmissionId}", record.EventSlug, record.SubmissionId);
    }
}
