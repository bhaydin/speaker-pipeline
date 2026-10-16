using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using SpeakerPipeline.Agents.Scoring;
using SpeakerPipeline.Notifications;

namespace SpeakerPipeline.Hosting.Functions.Functions;

/// <summary>
/// Manual ad-hoc trigger for the scoring agent. Auth required (function
/// key). Useful in dev for "run it now" without waiting for the timer.
/// Returns decisions to the caller and sends the current actionable snapshot,
/// with the same delivery deduplication as the scheduled run.
/// </summary>
public sealed class ScoringAgentHttpTrigger(PipelineRuns runs, ILogger<ScoringAgentHttpTrigger> logger)
{
    [Function("ScoringAgentHttpTrigger")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "scoring/run")] HttpRequestData request,
        CancellationToken ct)
    {
        logger.LogInformation("Scoring agent run starting (manual).");
        var verdicts = await runs.ScoringAsync(ct);

        var response = request.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { count = verdicts.Count, verdicts }, ct);
        return response;
    }
}
