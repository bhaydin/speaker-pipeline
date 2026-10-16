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
    public static IServiceCollection AddSpeakerPipelineStorage(this IServiceCollection services, IConfiguration configuration, string environmentName = "Production")
    {
        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<TableServiceClient>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
            RuntimeSafety.ValidateStorage(environmentName, RuntimeSafety.IsAzureHost, opts.ConnectionString, opts.TableEndpoint);

            // Local-dev / Azurite path. Supplied at runtime only; production
            // never sets a connection string — it uses managed identity below.
            if (!string.IsNullOrWhiteSpace(opts.ConnectionString))
            {
                var local = new TableServiceClient(opts.ConnectionString);
                if (!local.Uri.IsLoopback)
                    throw new InvalidOperationException("Development storage connection strings must target loopback Azurite.");
                return local;
            }

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
            if (opts.OpportunitiesEnabled)
                return new OpportunityEventRepository(sp.GetRequiredService<IOpportunityRepository>(), sp.GetRequiredService<TimeProvider>());
            var service = sp.GetRequiredService<TableServiceClient>();
            var client = service.GetTableClient(opts.EventsTableName);
            return new EventRepository(client, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<EventRepository>>());
        });

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IOpportunityRepository>(sp => new OpportunityRepository(
            sp.GetRequiredService<TableServiceClient>().GetTableClient(sp.GetRequiredService<IOptions<StorageOptions>>().Value.OpportunitiesTableName),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IOptions<StorageOptions>>().Value.OpportunitiesEnabled));
        services.AddSingleton<IOpportunityMigrationService, OpportunityMigrationService>();

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

        services.AddSingleton<ITopicRepository>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
            var service = sp.GetRequiredService<TableServiceClient>();
            var client = service.GetTableClient(opts.TopicsTableName);
            return new TopicRepository(client, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TopicRepository>>());
        });

        services.AddSingleton<IBlackoutRepository>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
            var service = sp.GetRequiredService<TableServiceClient>();
            var client = service.GetTableClient(opts.BlackoutsTableName);
            return new BlackoutRepository(client, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BlackoutRepository>>());
        });

        services.AddSingleton<INotificationLogRepository>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
            var service = sp.GetRequiredService<TableServiceClient>();
            var client = service.GetTableClient(opts.NotificationLogTableName);
            return new NotificationLogRepository(client, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<NotificationLogRepository>>());
        });

        return services;
    }
}
