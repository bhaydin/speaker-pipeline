using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SpeakerPipeline.Agents.Discovery;

/// <summary>
/// Google Programmable Search (Custom Search JSON API) adapter — the first
/// concrete <see cref="ISearchAdapter"/>. Returns candidate URLs only; the
/// discovery pipeline re-fetches and re-verifies each one. Swap for another
/// provider by registering a different ISearchAdapter — nothing else changes.
/// </summary>
public sealed class GoogleProgrammableSearchAdapter(
    HttpClient http,
    IOptions<SearchOptions> options,
    ILogger<GoogleProgrammableSearchAdapter> logger) : ISearchAdapter
{
    private readonly SearchOptions _options = options.Value;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int maxResults, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey) || string.IsNullOrWhiteSpace(_options.Cx))
        {
            logger.LogWarning("Search skipped: Google ApiKey/Cx not configured.");
            return [];
        }

        // The Custom Search API caps a single request at 10 results.
        var num = Math.Clamp(maxResults, 1, 10);
        var url = $"{_options.Endpoint}?key={Uri.EscapeDataString(_options.ApiKey)}" +
                  $"&cx={Uri.EscapeDataString(_options.Cx)}" +
                  $"&q={Uri.EscapeDataString(query)}&num={num}";

        try
        {
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("Search query degraded: '{Query}' -> {Status}", query, (int)resp.StatusCode);
                return [];
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            return ParseResponse(json, query);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Search query failed: '{Query}'", query);
            return [];
        }
    }

    /// <summary>
    /// Normalizes a Custom Search JSON payload into hits. Tolerant of a missing
    /// or empty <c>items</c> array (a valid "no results" response).
    /// </summary>
    internal static IReadOnlyList<SearchHit> ParseResponse(string json, string query)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<GoogleResponse>(json);
            var items = payload?.Items ?? [];
            return [.. items
                .Where(i => !string.IsNullOrWhiteSpace(i.Link))
                .Select(i => new SearchHit(i.Link!, i.Title, i.Snippet, query))];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed record GoogleResponse
    {
        [JsonPropertyName("items")]
        public List<GoogleItem>? Items { get; init; }
    }

    private sealed record GoogleItem
    {
        [JsonPropertyName("link")]
        public string? Link { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("snippet")]
        public string? Snippet { get; init; }
    }
}
