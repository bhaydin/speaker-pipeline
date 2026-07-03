using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SpeakerPipeline.Agents.Scoring;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddScoringAgent(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ScoringAgentOptions>()
            .Bind(configuration.GetSection(ScoringAgentOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<ScoringAgent>();
        return services;
    }
}
