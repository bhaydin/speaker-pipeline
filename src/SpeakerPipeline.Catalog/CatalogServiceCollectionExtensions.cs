using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SpeakerPipeline.Catalog;

public static class CatalogServiceCollectionExtensions
{
    public static IServiceCollection AddTalkCatalog(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CatalogOptions>().Bind(configuration.GetSection("Catalog"))
            .Validate(o => o.MaxExtractionCallsPerSync is >= 0 and <= 20 && o.MaxExtractionInputCharacters is >= 500 and <= 32000, "Catalog extraction budgets must be bounded.");
        services.AddHttpClient<ICatalogSource, GitHubCatalogSource>(http =>
        {
            http.BaseAddress = new Uri("https://api.github.com/");
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SpeakerPipeline-Catalog/1.0");
            http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            http.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddTransient<CatalogSyncService>();
        return services;
    }
}
