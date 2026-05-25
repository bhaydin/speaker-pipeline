namespace SpeakerPipeline.Agents.Scoring;

/// <summary>
/// Configuration bound from the "ScoringAgent" section. Defaults aim at
/// Azure AI Foundry but the IChatClient abstraction lets the binding swap
/// providers without code changes.
/// </summary>
public sealed class ScoringAgentOptions
{
    public const string SectionName = "ScoringAgent";

    public string ModelName { get; set; } = "<placeholder>";
    public string AgentName { get; set; } = "scoring-agent";
    public string AgentVersion { get; set; } = "v1";

    /// <summary>
    /// Maximum number of candidate events processed in one run before the
    /// agent batches and yields. Keeps token usage bounded.
    /// </summary>
    public int MaxCandidatesPerRun { get; set; } = 20;
}
