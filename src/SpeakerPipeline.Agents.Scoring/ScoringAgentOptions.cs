namespace SpeakerPipeline.Agents.Scoring;

/// <summary>
/// Configuration bound from the "ScoringAgent" section. Defaults aim at
/// Azure AI Foundry but the IChatClient abstraction lets the binding swap
/// providers without code changes.
/// </summary>
public sealed class ScoringAgentOptions
{
    public const string SectionName = "ScoringAgent";

    /// <summary>
    /// Deployment name to call, and the value reported as the model id on
    /// telemetry. For Azure AI Foundry this is the deployment (e.g. "gpt-5-mini",
    /// "model-router"), which is what <c>GetChatClient</c> binds to.
    /// </summary>
    public string ModelName { get; set; } = "<placeholder>";

    public string AgentName { get; set; } = "scoring-agent";
    public string AgentVersion { get; set; } = "v1";

    /// <summary>
    /// Chat provider binding: "foundry"/"azureopenai" (Azure AI Foundry via its
    /// Azure OpenAI endpoint). Selects how the <c>IChatClient</c> is constructed.
    /// </summary>
    public string Provider { get; set; } = "foundry";

    /// <summary>
    /// Provider endpoint, e.g. https://&lt;foundry&gt;.openai.azure.com/. Required
    /// for the foundry/azureopenai providers.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// Client id of the user-assigned managed identity to authenticate with.
    /// Optional — when empty, DefaultAzureCredential falls back to ambient
    /// resolution (the AZURE_CLIENT_ID app setting or a system-assigned identity).
    /// </summary>
    public string? ManagedIdentityClientId { get; set; }

    /// <summary>
    /// Maximum number of candidate events processed in one run before the
    /// agent batches and yields. Keeps token usage bounded.
    /// </summary>
    public int MaxCandidatesPerRun { get; set; } = 20;
}
