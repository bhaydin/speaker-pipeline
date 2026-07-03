using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.Mcp;
using Microsoft.Extensions.Logging;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Mcp.Tools;

/// <summary>
/// Pipeline read/command tools. <c>update_pipeline</c> takes a closed action
/// value (skip / monitor / intend / confirmed) — there is no ambiguous
/// "submit", which is the structural half of the ambiguity guard.
/// </summary>
public sealed class PipelineTools(ISpeakerPipelineApiClient api, ILogger<PipelineTools> logger)
{
    private const string ListToolName = "list_pipeline";
    private const string ListToolDescription =
        "List tracked events in the pipeline. Optionally filter by category (e.g. SubmitNow, Monitor, Submitted, Accepted).";

    private const string DeadlinesToolName = "get_upcoming_deadlines";
    private const string DeadlinesToolDescription =
        "List events whose CFP deadline falls within the next N days (default 30).";

    private const string UpdateToolName = "update_pipeline";
    private const string UpdateToolDescription =
        "Apply a pipeline action to an event by its slug. action must be one of: skip, monitor, intend (intend to submit), confirmed (already submitted).";

    [Function(ListToolName)]
    public async Task<string> ListPipeline(
        [McpToolTrigger(ListToolName, ListToolDescription)] ToolInvocationContext context,
        [McpToolProperty("filter", "Optional category filter, e.g. SubmitNow or Monitor.")] string? filter,
        CancellationToken ct)
    {
        IReadOnlyList<EventCategory>? categories = null;
        if (!string.IsNullOrWhiteSpace(filter) && Enum.TryParse<EventCategory>(filter, ignoreCase: true, out var cat))
        {
            categories = [cat];
        }

        var events = await api.GetEventsAsync(categories, ct: ct);
        return McpJson.Serialize(events);
    }

    [Function(DeadlinesToolName)]
    public async Task<string> GetUpcomingDeadlines(
        [McpToolTrigger(DeadlinesToolName, DeadlinesToolDescription)] ToolInvocationContext context,
        [McpToolProperty("days", "Look-ahead window in days (default 30).")] string? days,
        CancellationToken ct)
    {
        var window = int.TryParse(days, out var d) && d > 0 ? d : 30;
        var events = await api.GetEventsAsync(deadlineWindow: TimeSpan.FromDays(window), ct: ct);
        return McpJson.Serialize(events);
    }

    [Function(UpdateToolName)]
    public async Task<string> UpdatePipeline(
        [McpToolTrigger(UpdateToolName, UpdateToolDescription)] ToolInvocationContext context,
        [McpToolProperty("entity_id", "Slug of the event to update.", true)] string entityId,
        [McpToolProperty("action", "One of: skip, monitor, intend, confirmed.", true)] string action,
        [McpToolProperty("note", "Optional note recorded on the event.")] string? note,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entityId))
        {
            return "Error: 'entity_id' is required.";
        }

        if (!TryParseAction(action, out var parsed))
        {
            return $"Error: unknown action '{action}'. Use one of: skip, monitor, intend, confirmed.";
        }

        var updated = await api.ApplyPipelineActionAsync(entityId, new PipelineActionRequest { Action = parsed, Note = note }, ct);
        logger.LogInformation("Applied {Action} to {Slug} via MCP", parsed, entityId);
        return $"Applied '{parsed}' to '{updated.Slug}'. Category is now {updated.Category}.";
    }

    private static bool TryParseAction(string? value, out PipelineAction action)
    {
        action = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "skip":
                action = PipelineAction.Skip;
                return true;
            case "monitor":
                action = PipelineAction.Monitor;
                return true;
            case "intend":
            case "intend-to-submit":
            case "intend_to_submit":
                action = PipelineAction.Intend;
                return true;
            case "confirmed":
            case "submitted":
            case "submitted-confirmed":
            case "submitted_confirmed":
                action = PipelineAction.Confirmed;
                return true;
            default:
                return false;
        }
    }
}
