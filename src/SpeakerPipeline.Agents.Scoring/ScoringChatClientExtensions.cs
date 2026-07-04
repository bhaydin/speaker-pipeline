using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace SpeakerPipeline.Agents.Scoring;

/// <summary>
/// Registers the <see cref="IChatClient"/> the scoring agent consumes, bound to
/// the provider named in <see cref="ScoringAgentOptions"/>. Kept separate from
/// <see cref="ServiceCollectionExtensions.AddScoringAgent"/> on purpose: the eval
/// suite constructs the agent with its own deterministic client, so the runtime
/// model binding must be opt-in rather than baked into the agent registration.
/// </summary>
public static class ScoringChatClientExtensions
{
    public static IServiceCollection AddScoringChatClient(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ScoringAgentOptions>()
            .Bind(configuration.GetSection(ScoringAgentOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IChatClient>(sp =>
            CreateChatClient(sp.GetRequiredService<IOptions<ScoringAgentOptions>>().Value));

        return services;
    }

    /// <summary>
    /// Builds the chat client for the configured provider. Fails fast with an
    /// actionable message on missing/unsupported configuration rather than
    /// surfacing an opaque error on the first scoring call.
    /// </summary>
    internal static IChatClient CreateChatClient(ScoringAgentOptions options)
    {
        var provider = (options.Provider ?? string.Empty).Trim().ToLowerInvariant();
        switch (provider)
        {
            case "foundry":
            case "azureopenai":
                if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpointUri))
                {
                    throw new InvalidOperationException(
                        "ScoringAgent:Endpoint must be an absolute URI for the 'foundry'/'azureopenai' provider " +
                        "(e.g. https://<foundry>.openai.azure.com/).");
                }

                if (string.IsNullOrWhiteSpace(options.ModelName) || options.ModelName == "<placeholder>")
                {
                    throw new InvalidOperationException(
                        "ScoringAgent:ModelName must be a real Foundry deployment name " +
                        "(e.g. 'gpt-5-mini', 'model-router').");
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
                    $"ScoringAgent:Provider '{options.Provider}' is not supported. Use 'foundry' or 'azureopenai'.");
        }
    }
}
