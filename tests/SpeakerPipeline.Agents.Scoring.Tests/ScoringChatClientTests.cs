using Microsoft.Extensions.AI;
using SpeakerPipeline.Agents.Scoring;

namespace SpeakerPipeline.Agents.Scoring.Tests;

/// <summary>
/// Guards the fail-fast validation in the Foundry chat-client factory — the code
/// that decides whether the deployed scoring host can start. These are the exact
/// misconfigurations that took the function down before the binding was wired.
/// </summary>
public class ScoringChatClientTests
{
    private static ScoringAgentOptions Options(string provider = "foundry", string? endpoint = "https://foundry.openai.azure.com/", string model = "gpt-5-mini")
        => new() { Provider = provider, Endpoint = endpoint ?? string.Empty, ModelName = model };

    [Theory]
    [InlineData("foundry")]
    [InlineData("azureopenai")]
    [InlineData("FOUNDRY")] // case-insensitive
    public void CreateChatClient_valid_foundry_config_returns_client(string provider)
    {
        var client = ScoringChatClientExtensions.CreateChatClient(Options(provider: provider));
        Assert.IsAssignableFrom<IChatClient>(client);
    }

    [Theory]
    [InlineData("openai")]     // recognized name but not supported by this factory
    [InlineData("bedrock")]
    [InlineData("")]
    public void CreateChatClient_unsupported_provider_throws(string provider)
        => Assert.Throws<InvalidOperationException>(() => ScoringChatClientExtensions.CreateChatClient(Options(provider: provider)));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-uri")]
    public void CreateChatClient_missing_or_invalid_endpoint_throws(string endpoint)
        => Assert.Throws<InvalidOperationException>(() => ScoringChatClientExtensions.CreateChatClient(Options(endpoint: endpoint)));

    [Theory]
    [InlineData("")]
    [InlineData("<placeholder>")]
    public void CreateChatClient_missing_model_throws(string model)
        => Assert.Throws<InvalidOperationException>(() => ScoringChatClientExtensions.CreateChatClient(Options(model: model)));
}
