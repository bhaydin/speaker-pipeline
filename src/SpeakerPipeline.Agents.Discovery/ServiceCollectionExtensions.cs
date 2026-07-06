using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Agents.Discovery;

public static class ServiceCollectionExtensions
{
    private const string IngestHttpClientName = "discovery-ingest";

    public static IServiceCollection AddDiscoveryAgent(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DiscoveryOptions>()
            .Bind(configuration.GetSection(DiscoveryOptions.SectionName))
            .ValidateOnStart();

        // Extraction chat client (keyed so it can coexist with the scoring client).
        services.AddDiscoveryChatClient(configuration);

        // Typed HttpClient for fetching watchlist pages; factory-managed lifetime.
        services.AddHttpClient<WatchlistSource>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(20);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("SpeakerPipeline-DiscoveryAgent/1.0");
        });
        services.AddTransient<ISourceAdapter>(sp => sp.GetRequiredService<WatchlistSource>());

        // Targeted search-discovery. Provider-neutral (ISearchAdapter); Google
        // Programmable Search is the only concrete adapter today. SearchSource
        // no-ops when Search:Enabled is false, so registering it is always safe.
        services.AddOptions<SearchOptions>()
            .Bind(configuration.GetSection(SearchOptions.SectionName));
        services.AddHttpClient<ISearchAdapter, GoogleProgrammableSearchAdapter>(c =>
            c.Timeout = TimeSpan.FromSeconds(20));
        services.AddHttpClient<SearchSource>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(20);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("SpeakerPipeline-DiscoveryAgent/1.0");
        });
        services.AddTransient<ISourceAdapter>(sp => sp.GetRequiredService<SearchSource>());

        // confs.tech structured feed. Config-driven filters; no-ops when
        // ConfsTech:Enabled is false, so registering it is always safe. Maps JSON
        // straight to ExtractedEvent — no extraction model involved.
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<ConfsTechOptions>()
            .Bind(configuration.GetSection(ConfsTechOptions.SectionName));
        services.AddHttpClient<ConfsTechSource>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(20);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("SpeakerPipeline-DiscoveryAgent/1.0");
        });
        services.AddTransient<ISourceAdapter>(sp => sp.GetRequiredService<ConfsTechSource>());

        services.AddTransient(sp => new DiscoveryAgent(
            sp.GetRequiredKeyedService<IChatClient>(DiscoveryChatClientExtensions.DiscoveryClientKey),
            sp.GetRequiredService<ISpeakerPipelineApiClient>(),
            sp.GetServices<ISourceAdapter>(),
            sp.GetRequiredService<IOptions<DiscoveryOptions>>(),
            sp.GetRequiredService<IOptions<SearchOptions>>(),
            sp.GetRequiredService<ILogger<DiscoveryAgent>>()));

        // Manual ingestion lane: shares the discovery extraction model + reconcile
        // primitives; fetches a single vouched-for URL at a lower floor.
        services.AddHttpClient(IngestHttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(20);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("SpeakerPipeline-DiscoveryAgent/1.0");
        });
        services.AddTransient<IEventIngestService>(sp => new EventIngestor(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(IngestHttpClientName),
            sp.GetRequiredKeyedService<IChatClient>(DiscoveryChatClientExtensions.DiscoveryClientKey),
            sp.GetRequiredService<ISpeakerPipelineApiClient>(),
            sp.GetRequiredService<IOptions<DiscoveryOptions>>(),
            sp.GetRequiredService<ILogger<EventIngestor>>()));

        return services;
    }
}
