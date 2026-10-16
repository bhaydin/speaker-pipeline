using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Hosting.Functions.Functions;

/// <summary>Read-only verification of the deployed managed identity and API dependency.</summary>
public sealed class ReadinessHttpTrigger(ISpeakerPipelineApiClient api, IOperationsApiClient operations)
{
    [Function(nameof(ReadinessHttpTrigger))]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "readiness")] HttpRequestData request,
        CancellationToken ct)
    {
        // Never invoke agents here: a deployment probe must not mutate or notify.
        await api.GetEventsAsync(ct: ct);
        await api.GetTalksAsync(ct: ct);
        await api.GetTopicsAsync(ct: ct);
        await api.GetBlackoutsAsync(ct);
        // Exercises the consumer's managed identity against the mandatory operations
        // endpoint without creating records, invoking agents, or sending notifications.
        await operations.ListAsync("runs", DateTimeOffset.UtcNow, ct);
        var response = request.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { status = "ready" }, ct);
        return response;
    }
}
