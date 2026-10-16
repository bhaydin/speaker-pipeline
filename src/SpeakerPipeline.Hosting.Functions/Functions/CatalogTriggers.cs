using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Catalog;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Hosting.Functions.Functions;

public sealed class CatalogTriggers(ICatalogApiClient api, CatalogSyncService sync, IOptions<CatalogOptions> options, TimeProvider? time = null)
{
    [Function("CatalogWebhook")]
    public async Task<HttpResponseData> Webhook([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "catalog/github")] HttpRequestData request, CancellationToken ct)
    {
        if (!options.Value.Enabled) return request.CreateResponse(HttpStatusCode.ServiceUnavailable);
        string Header(string name) => request.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() ?? "" : "";
        var signature = Header("X-Hub-Signature-256");
        if (signature.Length != 71) return request.CreateResponse(HttpStatusCode.Unauthorized);
        using var reader = new StreamReader(request.Body);
        var chars = new char[1_000_001];
        var count = 0;
        while (count < chars.Length)
        {
            var read = await reader.ReadAsync(chars.AsMemory(count), ct);
            if (read == 0) break;
            count += read;
        }
        if (count > 1_000_000) return request.CreateResponse(HttpStatusCode.RequestEntityTooLarge);
        try
        {
            await api.AcceptCatalogWebhookAsync(new CatalogWebhookRequest(Header("X-GitHub-Delivery"), Header("X-GitHub-Event"), signature, new string(chars, 0, count)), ct);
            return request.CreateResponse(HttpStatusCode.Accepted);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
        { return request.CreateResponse(ex.StatusCode.Value); }
    }

    [Function("CatalogSyncWorker")]
    public Task<bool> Process([TimerTrigger("0 */2 * * * *")] TimerInfo timer, CancellationToken ct) => sync.ProcessNextAsync(ct);

    [Function("CatalogNightlySync")]
    public async Task Schedule([TimerTrigger("0 */15 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        if (options.Value.Enabled && CatalogSchedule.NightlyDeliveryId((time ?? TimeProvider.System).GetUtcNow()) is { } id)
            await api.EnqueueCatalogSyncAsync(new CatalogSyncRequest(id, "nightly-fallback"), ct);
    }
}
