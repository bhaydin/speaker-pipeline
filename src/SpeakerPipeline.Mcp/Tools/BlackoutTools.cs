using System.Globalization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.Mcp;
using Microsoft.Extensions.Logging;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Mcp.Tools;

/// <summary>
/// Feeds the conflict engine's system of record for unavailable dates.
/// </summary>
public sealed class BlackoutTools(ISpeakerPipelineApiClient api, ILogger<BlackoutTools> logger)
{
    private const string AddToolName = "add_blackout";
    private const string AddToolDescription =
        "Add a blackout date range Brian is unavailable (family, travel, etc.). Dates are ISO-8601 (yyyy-MM-dd).";

    [Function(AddToolName)]
    public async Task<string> AddBlackout(
        [McpToolTrigger(AddToolName, AddToolDescription)] ToolInvocationContext context,
        [McpToolProperty("start", "Start date, yyyy-MM-dd.", true)] string start,
        [McpToolProperty("end", "End date, yyyy-MM-dd (inclusive).", true)] string end,
        [McpToolProperty("reason", "Why the dates are blocked.", true)] string reason,
        CancellationToken ct)
    {
        if (!TryParseDate(start, out var startDate))
        {
            return $"Error: could not parse start date '{start}'. Use yyyy-MM-dd.";
        }

        if (!TryParseDate(end, out var endDate))
        {
            return $"Error: could not parse end date '{end}'. Use yyyy-MM-dd.";
        }

        if (endDate < startDate)
        {
            return "Error: end date must be on or after start date.";
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return "Error: 'reason' is required.";
        }

        var record = new BlackoutRecord
        {
            BlackoutId = SlugSanitizer.Sanitize($"{reason} {startDate:yyyy-MM-dd}"),
            StartDate = startDate,
            EndDate = endDate,
            Reason = reason.Trim(),
            Hardness = BlackoutHardness.Hard,
            Source = "mcp",
        };

        var saved = await api.UpsertBlackoutAsync(record, ct);
        logger.LogInformation("Added blackout {BlackoutId} via MCP", saved.BlackoutId);
        return $"Added blackout '{saved.Reason}' ({saved.StartDate:yyyy-MM-dd} to {saved.EndDate:yyyy-MM-dd}, id: {saved.BlackoutId}).";
    }

    private static bool TryParseDate(string? value, out DateTimeOffset date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            date = parsed;
            return true;
        }

        return false;
    }
}
