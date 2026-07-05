namespace SpeakerPipeline.Telegram.Tests;

public class TelegramTokenRedactorTests
{
    [Fact]
    public void Redact_removes_the_token_from_a_bot_api_url()
    {
        var url = "https://api.telegram.org/bot8123456789:AAH-abc_DEF123/sendMessage";

        var redacted = TelegramTokenRedactor.Redact(url);

        Assert.Equal("https://api.telegram.org/bot<redacted>/sendMessage", redacted);
        Assert.DoesNotContain("8123456789", redacted);
        Assert.DoesNotContain("AAH-abc_DEF123", redacted);
    }

    [Fact]
    public void Redact_leaves_unrelated_strings_untouched()
    {
        const string s = "https://func-speakerpipeline.azurewebsites.net/api/telegram/webhook";
        Assert.Equal(s, TelegramTokenRedactor.Redact(s));
    }

    [Theory]
    [InlineData("")]
    [InlineData("no token here")]
    public void Redact_handles_empty_and_tokenless_input(string input)
        => Assert.Equal(input, TelegramTokenRedactor.Redact(input));
}
