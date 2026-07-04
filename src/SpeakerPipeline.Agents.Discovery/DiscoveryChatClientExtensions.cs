using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace SpeakerPipeline.Agents.Discovery;

/// <summary>
/// Registers the extraction <see cref="IChatClient"/> for the discovery agent,
/// bound to Azure AI Foundry. Mirrors the scoring host's binding — a shared
/// Foundry chat-client factory (and the duplicated BearerTokenHandler) are a
/// noted consolidation follow-up once a third consumer exists.
/// </summary>
public static class DiscoveryChatClientExtensions
{
    public static IServiceCollection AddDiscoveryChatClient(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DiscoveryOptions>()
            .Bind(configuration.GetSection(DiscoveryOptions.SectionName));

        services.AddKeyedSingleton<IChatClient>(DiscoveryClientKey, (sp, _) =>
            CreateChatClient(sp.GetRequiredService<IOptions<DiscoveryOptions>>().Value));

        return services;
    }

    /// <summary>DI key so the discovery client doesn't collide with the scoring client in the same host.</summary>
    public const string DiscoveryClientKey = "discovery";

    internal static IChatClient CreateChatClient(DiscoveryOptions options)
    {
        var provider = (options.Provider ?? string.Empty).Trim().ToLowerInvariant();
        switch (provider)
        {
            case "foundry":
            case "azureopenai":
                if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpointUri))
                {
                    throw new InvalidOperationException(
                        "Discovery:Endpoint must be an absolute URI for the 'foundry'/'azureopenai' provider " +
                        "(e.g. https://<foundry>.openai.azure.com/).");
                }

                if (string.IsNullOrWhiteSpace(options.ModelName) || options.ModelName == "<placeholder>")
                {
                    throw new InvalidOperationException(
                        "Discovery:ModelName must be a real Foundry deployment name (e.g. 'gpt-5-nano').");
                }

                var credential = string.IsNullOrWhiteSpace(options.ManagedIdentityClientId)
                    ? new DefaultAzureCredential()
                    : new DefaultAzureCredential(new DefaultAzureCredentialOptions
                    {
                        ManagedIdentityClientId = options.ManagedIdentityClientId,
                    });

                return new AzureOpenAIClient(endpointUri, credential)
                    .GetChatClient(options.ModelName)
                    .AsIChatClient();

            default:
                throw new InvalidOperationException(
                    $"Discovery:Provider '{options.Provider}' is not supported. Use 'foundry' or 'azureopenai'.");
        }
    }
}
