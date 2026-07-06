using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Mcp.Ingest;

/// <summary>
/// The MCP host's route to the manual ingestion lane. Ingest extraction lives in
/// the agent tier (fetch → LLM → reconcile), so this posts to the discovery
/// host's <c>/discovery/ingest</c> endpoint over HTTP rather than re-implementing
/// extraction. Keeps the "agents do the LLM, everyone else is thin" separation.
/// </summary>
public sealed class HttpEventIngestService(
    HttpClient http,
    IOptions<IngestOptions> options,
    ILogger<HttpEventIngestService> logger) : IEventIngestService
{
    private readonly IngestOptions _options = options.Value;

    public async Task<IngestResult> IngestAsync(string url, string? note = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Endpoint))
        {
            return IngestResult.Failed(IngestStatus.FetchFailed, "Ingest endpoint is not configured.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = JsonContent.Create(new { url, note }),
        };
        if (!string.IsNullOrWhiteSpace(_options.FunctionKey))
        {
            request.Headers.Add("x-functions-key", _options.FunctionKey);
        }

        try
        {
            using var resp = await http.SendAsync(request, ct);

            // The endpoint returns an IngestResult body on both 200 and 422 (not-an-event etc.),
            // so parse whenever there's content; only fall back on transport/5xx failures.
            if (resp.Content.Headers.ContentLength is > 0 or null)
            {
                var result = await resp.Content.ReadFromJsonAsync<IngestResult>(ct);
                if (result is not null)
                {
                    return result;
                }
            }

            logger.LogWarning("Ingest call to {Endpoint} returned {Status} with no parseable body", _options.Endpoint, (int)resp.StatusCode);
            return IngestResult.Failed(IngestStatus.FetchFailed, $"Ingest service returned {(int)resp.StatusCode}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Ingest call to {Endpoint} failed", _options.Endpoint);
            return IngestResult.Failed(IngestStatus.FetchFailed, "Couldn't reach the ingest service.");
        }
    }
}

/// <summary>Config for reaching the discovery host's ingest endpoint (bound from "Ingest").</summary>
public sealed class IngestOptions
{
    public const string SectionName = "Ingest";

    /// <summary>Full URL of the discovery host's ingest endpoint, e.g. <c>https://&lt;host&gt;/api/discovery/ingest</c>.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Function key for the endpoint — supply from Key Vault, never in code.</summary>
    public string? FunctionKey { get; set; }
}
