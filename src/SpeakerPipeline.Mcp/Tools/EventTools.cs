using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.Mcp;
using Microsoft.Extensions.Logging;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Mcp.Tools;

/// <summary>
/// Manual event-ingestion tool. Lets any AI session that has found a promising
/// CFP ("find me AI CFPs closing this month") turn a keeper into pipeline state
/// with one call, instead of leaving it in chat scrollback. Thin adapter over
/// <see cref="IEventIngestService"/> — extraction happens in the agent tier.
/// </summary>
public sealed class EventTools(IEventIngestService ingest, ILogger<EventTools> logger)
{
    private const string IngestToolName = "ingest_event";
    private const string IngestToolDescription =
        "Ingest a speaking-opportunity URL (a CFP or event page) into the pipeline. Fetches and extracts the page, "
        + "tracks it as a scoring candidate, and returns what was captured. Use per keeper when researching CFPs.";

    [Function(IngestToolName)]
    public async Task<string> IngestEvent(
        [McpToolTrigger(IngestToolName, IngestToolDescription)] ToolInvocationContext context,
        [McpToolProperty("url", "URL of the CFP or event page to track.", true)] string url,
        [McpToolProperty("note", "Optional note on why this is worth tracking.")] string? note,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "Error: 'url' is required.";
        }

        var result = await ingest.IngestAsync(url.Trim(), string.IsNullOrWhiteSpace(note) ? null : note.Trim(), ct);
        logger.LogInformation("Ingest via MCP: {Url} -> {Status}", url, result.Status);
        return McpJson.Serialize(result);
    }
}
