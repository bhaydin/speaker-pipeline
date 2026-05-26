using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Storage;

/// <summary>
/// DI wiring for SpeakerPipeline.Storage. Public so the API project can call
/// it; everything else in this project is internal.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSpeakerPipelineStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<TableServiceClient>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
            if (string.IsNullOrWhiteSpace(opts.TableEndpoint))
            {
                throw new InvalidOperationException(
                    "Storage:TableEndpoint is required, e.g. 'https://<account>.table.core.windows.net'. " +
                    "Set it via configuration; never embed account keys in code.");
            }

            return new TableServiceClient(new Uri(opts.TableEndpoint), new DefaultAzureCredential());
        });

        services.AddSingleton<IEventRepository>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
            var service = sp.GetRequiredService<TableServiceClient>();
            var client = service.GetTableClient(opts.EventsTableName);
            return new EventRepository(client, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<EventRepository>>());
        });

        services.AddSingleton<ISubmissionRepository>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
            var service = sp.GetRequiredService<TableServiceClient>();
            var client = service.GetTableClient(opts.SubmissionsTableName);
            return new SubmissionRepository(client, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SubmissionRepository>>());
        });

        services.AddSingleton<ITalkRepository>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
            var service = sp.GetRequiredService<TableServiceClient>();
            var client = service.GetTableClient(opts.TalksTableName);
            return new TalkRepository(client, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TalkRepository>>());
        });

        return services;
    }
}
