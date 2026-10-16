using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SpeakerPipeline.Telegram;

/// <summary>Never log the request URI: the Telegram credential is in its path.</summary>
public sealed class TelegramClient(HttpClient http, IOptions<TelegramOptions> options, ILogger<TelegramClient> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly TelegramOptions _options = options.Value;

    public async Task SendMessageAsync(long chatId, string text, CancellationToken ct = default)
        => await SendAsync(chatId, text, null, false, ct);

    public async Task<TelegramSentMessage> SendAsync(long chatId, string text, TelegramKeyboard? keyboard, bool silent, CancellationToken ct)
    {
        var result = await CallAsync("sendMessage", new { chat_id = chatId, text, parse_mode = "HTML",
            link_preview_options = new { is_disabled = true }, disable_notification = silent, reply_markup = keyboard }, ct);
        TelegramSentMessage? sent;
        try { sent = result.Deserialize<TelegramSentMessage>(Json); }
        catch (JsonException) { throw new HttpRequestException("Telegram send response was ambiguous."); }
        return sent is { MessageId: > 0 } ? sent : throw new HttpRequestException("Telegram returned no sent message receipt.");
    }

    public async Task EditAsync(long chatId, long messageId, string text, TelegramKeyboard? keyboard, CancellationToken ct)
    {
        try
        {
            await CallAsync("editMessageText", new { chat_id = chatId, message_id = messageId, text, parse_mode = "HTML",
                link_preview_options = new { is_disabled = true }, reply_markup = keyboard ?? new TelegramKeyboard([]) }, ct);
        }
        catch (TelegramApiException ex) when (ex.NotModified) { }
    }

    public async Task AnswerAsync(string callbackId, string? text, bool alert, CancellationToken ct)
        => await CallAsync("answerCallbackQuery", new { callback_query_id = callbackId, text, show_alert = alert }, ct);

    private async Task<JsonElement> CallAsync(string method, object payload, CancellationToken ct)
    {
        try { return await CallCoreAsync(method, payload, ct); }
        catch (TelegramApiException) { throw; }
        catch (HttpRequestException ex)
        {
            // A transport exception can include the complete credential-bearing URL.
            // Do not retain it as InnerException or copy its message into host logs.
            throw new HttpRequestException("Telegram transport failed.", null, ex.StatusCode);
        }
        catch (OperationCanceledException)
        {
            throw new OperationCanceledException("Telegram transport was canceled; delivery may be uncertain.", ct);
        }
        catch (IOException)
        {
            throw new HttpRequestException("Telegram transport response was incomplete.");
        }
        catch (InvalidOperationException)
        {
            throw new HttpRequestException("Telegram response could not confirm the operation.");
        }
    }

    private async Task<JsonElement> CallCoreAsync(string method, object payload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.BotToken)) throw new InvalidOperationException("Telegram is not configured.");
        using var response = await http.PostAsJsonAsync($"https://api.telegram.org/bot{_options.BotToken}/{method}", payload, Json, ct);
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); }
        catch (JsonException)
        {
            throw new HttpRequestException("Telegram returned an unreadable response.", null,
                response.IsSuccessStatusCode ? null : response.StatusCode);
        }
        using var body = parsed;
        var root = body.RootElement;
        if (!response.IsSuccessStatusCode || !root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            var description = root.TryGetProperty("description", out var value) ? value.GetString() : "";
            logger.LogWarning("Telegram operation {Method} failed with status {Status}.", method, (int)response.StatusCode);
            throw new TelegramApiException(response.StatusCode, description?.Contains("message is not modified", StringComparison.OrdinalIgnoreCase) == true);
        }
        return root.TryGetProperty("result", out var result) ? result.Clone() : default;
    }
}

public sealed class TelegramApiException(System.Net.HttpStatusCode status, bool notModified = false)
    : HttpRequestException($"Telegram request failed ({(int)status}).", null, status)
{
    public bool NotModified { get; } = notModified;
}
