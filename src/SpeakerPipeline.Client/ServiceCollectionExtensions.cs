using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Client;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the HttpClient-backed <see cref="ISpeakerPipelineApiClient"/>.
    /// Caller is expected to configure the base address (via the
    /// "SpeakerPipelineApi:BaseUrl" configuration key) and add any auth header
    /// handlers on the returned builder.
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
