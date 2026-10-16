using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Api.Auth;
using SpeakerPipeline.Core;
using SpeakerPipeline.Storage;

namespace SpeakerPipeline.Api.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/health", () => Results.Ok(new { status = "ok" }))
            .AllowAnonymous()
            .WithTags("Health")
            .WithName("Health");
        routes.MapGet("/health/ready", async (IEventRepository events, ITalkRepository talks,
            ISubmissionRepository submissions, ITopicRepository topics, IBlackoutRepository blackouts,
            INotificationLogRepository notifications, IServiceProvider services, IConfiguration configuration,
            IHostEnvironment environment, CancellationToken ct) =>
        {
            var storage = services.GetRequiredService<IOptions<StorageOptions>>().Value;
            // Operational receipts/run records are mandatory for the new consumers.
            // Production also requires explicit acknowledgement so maintenance includes
            // the table. Local configurations can omit that flag, but cannot skip the probe.
            if (!environment.IsDevelopment() && !storage.OperationsProvisioned)
                return Results.Problem("Operations storage must be provisioned and Storage:OperationsProvisioned enabled before releasing consumers.", statusCode: 503);
            await foreach (var _ in events.QueryAsync(ct)) { break; }
            await talks.GetAllAsync(ct);
            await submissions.GetForEventAsync("readiness-probe", ct);
            await topics.GetAllAsync(ct);
            await blackouts.GetAllAsync(ct);
            await notifications.GetForPeriodAsync(DateTimeOffset.UtcNow.ToString("yyyy-MM"), ct);
            var extraTables = new List<string> { storage.OperationsTableName };
            if (storage.OpportunitiesProvisioned || storage.OpportunitiesEnabled) extraTables.Add(storage.OpportunitiesTableName);
            if (storage.CatalogProvisioned || configuration.GetValue<bool>("Catalog:Enabled")) extraTables.Add(storage.CatalogTableName);
            if (extraTables.Count > 0)
            {
                var tables = services.GetRequiredService<TableServiceClient>();
                try
                {
                    foreach (var name in extraTables)
                        await foreach (var _ in tables.GetTableClient(name).QueryAsync<TableEntity>(maxPerPage: 1, select: ["PartitionKey"], cancellationToken: ct)) { break; }
                }
                catch (RequestFailedException)
                {
                    return Results.Problem("Required storage is unavailable or access was denied.", statusCode: 503);
                }
                if (storage.OpportunitiesEnabled)
                {
                    var marker = await tables.GetTableClient(storage.OpportunitiesTableName).GetEntityIfExistsAsync<TableEntity>("__migration", "ready", cancellationToken: ct);
                    if (!marker.HasValue || string.IsNullOrEmpty(marker.Value!.GetString("SnapshotChecksum")))
                        return Results.Problem("Opportunity migration has not completed.", statusCode: 503);
                }
            }
            return Results.Ok(new { status = "ready" });
        }).RequireAuthorization(AuthExtensions.PolicyName).WithTags("Health").WithName("Readiness");
        return routes;
    }
}
