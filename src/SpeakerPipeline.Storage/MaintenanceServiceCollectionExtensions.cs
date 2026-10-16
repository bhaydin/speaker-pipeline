using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Storage;

public static class MaintenanceServiceCollectionExtensions
{
    public static IServiceCollection AddPipelineMaintenanceStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IMaintenanceStore>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
            var tables = sp.GetRequiredService<TableServiceClient>();
            var target = new MaintenanceTarget
            {
                TableEndpoint = tables.Uri.AbsoluteUri.TrimEnd('/'),
                TableNames = new(StringComparer.Ordinal)
                {
                    ["Events"] = options.EventsTableName, ["Submissions"] = options.SubmissionsTableName,
                    ["Talks"] = options.TalksTableName, ["Topics"] = options.TopicsTableName,
                    ["Blackouts"] = options.BlackoutsTableName, ["NotificationLog"] = options.NotificationLogTableName,
                },
            };
            if (target.TableNames.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != MaintenanceIntegrity.TableNames.Length)
                throw new InvalidOperationException("Maintenance requires six distinct business tables.");
            BlobServiceClient blobs;
            if (!string.IsNullOrWhiteSpace(options.ConnectionString)) blobs = new BlobServiceClient(options.ConnectionString);
            else
            {
                var endpoint = configuration["Maintenance:BlobEndpoint"];
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    if (!tables.Uri.Host.Contains(".table.", StringComparison.Ordinal))
                        throw new InvalidOperationException("Maintenance:BlobEndpoint is required for this Table endpoint.");
                    var uri = new UriBuilder(tables.Uri) { Host = tables.Uri.Host.Replace(".table.", ".blob.", StringComparison.Ordinal), Path = "" };
                    endpoint = uri.Uri.AbsoluteUri;
                }
                blobs = new BlobServiceClient(new Uri(endpoint), new DefaultAzureCredential());
            }
            if (tables.Uri.IsLoopback != blobs.Uri.IsLoopback)
                throw new InvalidOperationException("Maintenance Table and Blob endpoints must both target loopback Azurite or both target cloud storage.");
            var container = blobs.GetBlobContainerClient(configuration["Maintenance:JournalContainer"] ?? "artifacts");
            return new AzureMaintenanceStore(tables, container, target);
        });
        services.AddSingleton<IPipelineMaintenanceService, PipelineMaintenanceService>();
        return services;
    }
}
