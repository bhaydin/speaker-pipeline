using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Telegram;

namespace SpeakerPipeline.Hosting.Functions.Functions;

/// <summary>
/// Inbound Telegram webhook. Anonymous at the Functions layer, but fails closed:
/// it requires the shared secret Telegram echoes in the
/// <c>X-Telegram-Bot-Api-Secret-Token</c> header (set at setWebhook time), so the
/// endpoint isn't actually open. Parses the update, routes it to a pipeline
/// action, and sends the reply. Returns 401 for missing/invalid secrets; otherwise
/// returns 200 (even on JSON parse failures) so Telegram doesn't retry-storm.
/// </summary>
public sealed class TelegramWebhook(
    TelegramCommandRouter router,
    TelegramClient client,
    IOptions<TelegramOptions> options,
    ILogger<TelegramWebhook> logger)
{
    private const string SecretHeader = "X-Telegram-Bot-Api-Secret-Token";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly TelegramOptions _options = options.Value;

    [Function("TelegramWebhook")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "telegram/webhook")] HttpRequestData request,
        CancellationToken ct)
    {
        // Kill switch: when Telegram is disabled, ignore updates but return 200 so Telegram doesn't retry.
        if (!_options.Enabled)
        {
            logger.LogInformation("Telegram webhook: Telegram is disabled; ignoring update.");
            return request.CreateResponse(HttpStatusCode.OK);
        }

        // Fail closed: with no configured secret we cannot trust any caller.
        if (string.IsNullOrEmpty(_options.WebhookSecret))
        {
            logger.LogError("Telegram webhook: no WebhookSecret configured; rejecting.");
            return request.CreateResponse(HttpStatusCode.Unauthorized);
        }

        var provided = request.Headers.TryGetValues(SecretHeader, out var values) ? values.FirstOrDefault() : null;
        if (!string.Equals(provided, _options.WebhookSecret, StringComparison.Ordinal))
        {
            logger.LogWarning("Telegram webhook: bad or missing secret token.");
            return request.CreateResponse(HttpStatusCode.Unauthorized);
        }

        TelegramUpdate? update;
        try
        {
            update = await JsonSerializer.DeserializeAsync<TelegramUpdate>(request.Body, JsonOptions, ct);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Telegram webhook: unparseable update body.");
            return request.CreateResponse(HttpStatusCode.OK);
        }

        if (update is not null)
        {
            var reply = await router.RouteAsync(update, ct);
            if (reply is not null)
            {
                await client.SendMessageAsync(reply.ChatId, reply.Text, ct);
            }
        }

        return request.CreateResponse(HttpStatusCode.OK);
    }
}
