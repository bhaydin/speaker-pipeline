using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using SpeakerPipeline.Agents.Discovery;

namespace SpeakerPipeline.Hosting.Functions.Functions;

/// <summary>
/// Manual ad-hoc trigger for the discovery agent. Auth required (function key).
/// Fetches the watchlist, extracts events, and reconciles them into the tracker.
/// </summary>
public sealed class DiscoveryHttpTrigger(DiscoveryAgent agent, ILogger<DiscoveryHttpTrigger> logger)
{
    [Function("DiscoveryHttpTrigger")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "discovery/run")] HttpRequestData request,
        CancellationToken ct)
    {
        logger.LogInformation("Discovery run starting (manual).");
        var results = await agent.RunAsync(ct);

        var response = request.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { count = results.Count, results }, ct);
        return response;
    }
}
