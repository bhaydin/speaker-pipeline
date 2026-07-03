using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.Mcp;
using Microsoft.Extensions.Logging;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Mcp.Tools;

/// <summary>
/// Topic-funnel tools: inject an idea from any AI conversation, and read the
/// funnel back. Thin adapters over <see cref="ISpeakerPipelineApiClient"/>.
/// </summary>
public sealed class TopicTools(ISpeakerPipelineApiClient api, ILogger<TopicTools> logger)
{
    private const string AddToolName = "add_topic_idea";
    private const string AddToolDescription =
        "Add a talk topic idea to the pipeline funnel. Use when an idea worth speaking about surfaces mid-conversation.";

    private const string ListToolName = "list_topics";
    private const string ListToolDescription =
        "List topic ideas in the funnel. Optionally filter by stage (Idea, Validated, Built).";

    [Function(AddToolName)]
    public async Task<string> AddTopicIdea(
        [McpToolTrigger(AddToolName, AddToolDescription)] ToolInvocationContext context,
        [McpToolProperty("title", "Short title of the topic idea.", true)] string title,
        [McpToolProperty("one_liner", "One-sentence description of the idea.")] string? oneLiner,
        [McpToolProperty("lane", "Portfolio lane: AgentOps, HybridAgents, M365Governance, or PracticalEnterpriseAI.")] string? lane,
        [McpToolProperty("notes", "Any extra context.")] string? notes,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "Error: 'title' is required.";
        }

        Lane? parsedLane = Enum.TryParse<Lane>(lane, ignoreCase: true, out var l) ? l : null;

        var record = new TopicRecord
        {
            TopicId = SlugSanitizer.Sanitize(title),
            Title = title.Trim(),
            OneLiner = string.IsNullOrWhiteSpace(oneLiner) ? null : oneLiner.Trim(),
            Stage = TopicStage.Idea,
            Source = TopicSource.Claude,
            Lane = parsedLane,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
        };

        var saved = await api.UpsertTopicAsync(record, ct);
        logger.LogInformation("Added topic idea {TopicId} via MCP", saved.TopicId);
        return $"Added topic idea '{saved.Title}' (id: {saved.TopicId}, stage: {saved.Stage}).";
    }

    [Function(ListToolName)]
    public async Task<string> ListTopics(
        [McpToolTrigger(ListToolName, ListToolDescription)] ToolInvocationContext context,
        [McpToolProperty("stage", "Optional stage filter: Idea, Validated, or Built.")] string? stage,
        CancellationToken ct)
    {
        TopicStage? parsedStage = Enum.TryParse<TopicStage>(stage, ignoreCase: true, out var s) ? s : null;
        var topics = await api.GetTopicsAsync(parsedStage, ct);
        return McpJson.Serialize(topics);
    }
}
