using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Storage;

public static class OperationsStorageRegistration
{
    public static IServiceCollection AddPipelineOperations(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IOperationsRepository>(sp => new OperationsRepository(
            sp.GetRequiredService<TableServiceClient>().GetTableClient(configuration["Storage:OperationsTableName"] ?? "Operations")));
        return services;
    }
}

internal sealed class OperationsRepository(TableClient table) : IOperationsRepository
{
    public async Task<VersionedOperation?> GetAsync(string kind, string id, CancellationToken ct = default)
    {
        var result = await table.GetEntityIfExistsAsync<TableEntity>(kind, id, cancellationToken: ct);
        return result.HasValue ? Read(result.Value!) : null;
    }

    public async Task<VersionedOperation?> SaveAsync(OperationDocument document, string? expectedVersion = null, CancellationToken ct = default)
    {
        var entity = new TableEntity(document.Kind, document.Id)
        {
            ["Payload"] = document.Payload,
            ["UpdatedUtc"] = document.UpdatedUtc,
        };
        try
        {
            var response = expectedVersion is null
                ? await table.AddEntityAsync(entity, ct)
                : await table.UpdateEntityAsync(entity, new ETag(expectedVersion), TableUpdateMode.Replace, ct);
            return new VersionedOperation(document, response.Headers.ETag?.ToString()
                ?? throw new InvalidOperationException("Operation write returned no version."));
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412) { return null; }
    }

    public async Task<IReadOnlyList<VersionedOperation>> ListAsync(string kind, DateTimeOffset since, CancellationToken ct = default)
    {
        var rows = new List<VersionedOperation>();
        await foreach (var entity in table.QueryAsync<TableEntity>(
            TableClient.CreateQueryFilter($"PartitionKey eq {kind} and UpdatedUtc ge {since}"), cancellationToken: ct))
            rows.Add(Read(entity));
        return rows.OrderByDescending(r => r.Document.UpdatedUtc).ToArray();
    }

    private static VersionedOperation Read(TableEntity entity) => new(
        new OperationDocument(entity.PartitionKey, entity.RowKey, entity.GetString("Payload") ?? "{}",
            entity.GetDateTimeOffset("UpdatedUtc") ?? DateTimeOffset.MinValue), entity.ETag.ToString());
}
