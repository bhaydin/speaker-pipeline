using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Agents.Discovery;

public static class ServiceCollectionExtensions
{
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

        services.AddTransient(sp => new DiscoveryAgent(
            sp.GetRequiredKeyedService<IChatClient>(DiscoveryChatClientExtensions.DiscoveryClientKey),
            sp.GetRequiredService<ISpeakerPipelineApiClient>(),
            sp.GetServices<ISourceAdapter>(),
            sp.GetRequiredService<IOptions<DiscoveryOptions>>(),
            sp.GetRequiredService<ILogger<DiscoveryAgent>>()));

        return services;
    }
}
