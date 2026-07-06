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
/// Sends a run-summary notification so a manual run confirms itself.
/// </summary>
public sealed class ScoringAgentHttpTrigger(ScoringAgent agent, INotifier notifier, ILogger<ScoringAgentHttpTrigger> logger)
{
    [Function("ScoringAgentHttpTrigger")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "scoring/run")] HttpRequestData request,
        CancellationToken ct)
    {
        logger.LogInformation("Scoring agent run starting (manual).");
        var verdicts = await agent.RunAsync(ct);
        await ScoringNotification.SendAsync(notifier, verdicts, ct);

        var response = request.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { count = verdicts.Count, verdicts }, ct);
        return response;
    }
}
