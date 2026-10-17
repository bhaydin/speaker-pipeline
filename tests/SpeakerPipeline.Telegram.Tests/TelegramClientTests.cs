using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace SpeakerPipeline.Telegram.Tests;

public sealed class TelegramClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Command_reply_omits_absent_keyboard_in_the_HTTP_request(bool silent)
    {
        using var transport = new CaptureRequest();
        var client = Client(transport);

        var receipt = await client.SendAsync(7, "<b>Pipeline</b>\nNo events tracked yet.", null, silent, default);

        Assert.Equal(42, receipt.MessageId);
        Assert.Equal("sendMessage", transport.Method);
        Assert.False(transport.Body.TryGetProperty("reply_markup", out _));
        Assert.Equal("HTML", transport.Body.GetProperty("parse_mode").GetString());
        Assert.Equal(silent, transport.Body.GetProperty("disable_notification").GetBoolean());
        Assert.True(transport.Body.GetProperty("link_preview_options").GetProperty("is_disabled").GetBoolean());
    }

    [Fact]
    public async Task Actionable_message_preserves_keyboard_and_callback_data()
    {
        using var transport = new CaptureRequest();
        var client = Client(transport);
        var keyboard = new TelegramKeyboard([[new TelegramButton("Drop", "p:0123456789abcdef01234567:d")]]);

        await client.SendAsync(7, "<b>Opportunity</b>", keyboard, false, default);

        var button = transport.Body.GetProperty("reply_markup").GetProperty("inline_keyboard")[0][0];
        Assert.Equal("Drop", button.GetProperty("text").GetString());
        Assert.Equal("p:0123456789abcdef01234567:d", button.GetProperty("callback_data").GetString());
    }

    [Fact]
    public async Task Editing_without_keyboard_explicitly_removes_previous_buttons()
    {
        using var transport = new CaptureRequest();
        var client = Client(transport);

        await client.EditAsync(7, 42, "Submitted", null, default);

        Assert.Equal("editMessageText", transport.Method);
        Assert.Equal(42, transport.Body.GetProperty("message_id").GetInt64());
        Assert.Empty(transport.Body.GetProperty("reply_markup").GetProperty("inline_keyboard").EnumerateArray());
    }

    private static TelegramClient Client(HttpMessageHandler transport) => new(new HttpClient(transport),
        Options.Create(new TelegramOptions { BotToken = "SYNTHETIC_TEST_TOKEN" }), NullLogger<TelegramClient>.Instance);

    private sealed class CaptureRequest : HttpMessageHandler
    {
        public string? Method { get; private set; }
        public JsonElement Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.RequestUri!.Segments[^1];
            using var parsed = await JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            Body = parsed.RootElement.Clone();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true,\"result\":{\"message_id\":42,\"chat\":{\"id\":7}}}")
            };
        }
    }
}
