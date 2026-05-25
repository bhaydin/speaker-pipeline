using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SpeakerPipeline.Agents.Scoring.ApiClient;
using SpeakerPipeline.Core;

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

    /// <summary>
    /// Registers the HttpClient-backed <see cref="ISpeakerPipelineApiClient"/>.
    /// Caller is expected to configure the base address and any auth header
    /// handlers on the named client.
    /// </summary>
    public static IHttpClientBuilder AddSpeakerPipelineApiClient(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("SpeakerPipelineApi");

        return services.AddHttpClient<ISpeakerPipelineApiClient, SpeakerPipelineApiClient>(client =>
        {
            var baseUrl = section["BaseUrl"];
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                client.BaseAddress = new Uri(baseUrl);
            }

            client.Timeout = TimeSpan.FromSeconds(30);
        });
    }
}
