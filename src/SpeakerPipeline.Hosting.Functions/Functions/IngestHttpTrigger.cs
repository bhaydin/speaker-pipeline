using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Hosting.Functions.Functions;

/// <summary>
/// The manual ingestion endpoint (BUILD_PLAN.GO_FORWARD A2). Fetches a single
/// human-supplied URL, extracts + reconciles it at the human-vouched floor, and
/// returns the outcome. Auth required (function key). Both the MCP
/// <c>ingest_event</c> tool and the Telegram <c>/track</c> command reach the one
/// ingest implementation through this endpoint (MCP over HTTP, Telegram
/// in-process via the same <see cref="IEventIngestService"/>).
/// </summary>
public sealed class IngestHttpTrigger(IEventIngestService ingest, ILogger<IngestHttpTrigger> logger)
{
    [Function("IngestHttpTrigger")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "discovery/ingest")] HttpRequestData request,
        CancellationToken ct)
    {
        var body = await request.ReadFromJsonAsync<IngestRequest>(ct);
        if (body is null || string.IsNullOrWhiteSpace(body.Url))
        {
            var bad = request.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteAsJsonAsync(new { error = "Body must include a non-empty 'url'." }, ct);
            return bad;
        }

        logger.LogInformation("Ingest requested for {Url}", body.Url);
        var result = await ingest.IngestAsync(body.Url, body.Note, ct);

        // The URL was well-formed but not usable — report as 422 so callers can distinguish
        // a bad request from a page that simply wasn't an event.
        var status = result.Status is IngestStatus.FetchFailed or IngestStatus.NotAnEvent or IngestStatus.LowConfidence
            ? HttpStatusCode.UnprocessableEntity
            : HttpStatusCode.OK;

        var response = request.CreateResponse(status);
        await response.WriteAsJsonAsync(result, ct);
        return response;
    }

    private sealed record IngestRequest(string Url, string? Note);
}
