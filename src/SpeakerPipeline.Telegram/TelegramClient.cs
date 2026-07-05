using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SpeakerPipeline.Telegram;

/// <summary>
/// Thin wrapper over the Telegram Bot API <c>sendMessage</c> method, shared by
/// the outbound lane and the webhook's replies. No-ops (rather than throwing)
/// when no bot token is configured.
/// </summary>
/// <remarks>
/// The Bot API only authenticates via the token in the URL path — there is no
/// header form. That means the token can surface in HTTP traces; hardening step
/// is a telemetry redaction processor scoped to api.telegram.org. Tracked as a
/// follow-up, not wired here.
/// </remarks>
public sealed class TelegramClient(
    HttpClient http,
    IOptions<TelegramOptions> options,
    ILogger<TelegramClient> logger)
{
    private readonly TelegramOptions _options = options.Value;

    public async Task SendMessageAsync(long chatId, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.BotToken))
        {
            logger.LogWarning("Telegram send skipped: no bot token configured.");
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            chat_id = chatId,
            text,
            parse_mode = "HTML",
            disable_web_page_preview = true,
        });

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://api.telegram.org/bot{_options.BotToken}/sendMessage")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Telegram send failed: {Status} {Body}", (int)response.StatusCode, body);
            response.EnsureSuccessStatusCode();
        }
}
