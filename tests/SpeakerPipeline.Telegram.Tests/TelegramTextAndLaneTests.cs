using SpeakerPipeline.Notifications;

namespace SpeakerPipeline.Telegram.Tests;

public class TelegramTextAndLaneTests
{
    [Theory]
    [InlineData("Agentic RAG eval patterns!", "agentic-rag-eval-patterns")]
    [InlineData("  Multiple   spaces & symbols  ", "multiple-spaces-symbols")]
    [InlineData("C#/.NET 10", "c-net-10")]
    public void Slugify_produces_url_safe_slugs(string input, string expected)
        => Assert.Equal(expected, TelegramText.Slugify(input));

    [Fact]
    public void Escape_encodes_telegram_html_sensitive_chars()
        => Assert.Equal("&lt;b&gt;&amp;", TelegramText.Escape("<b>&"));

    [Fact]
    public void StripHtml_flattens_blocks_to_lines_and_drops_tags()
    {
        var text = TelegramText.StripHtml("<p>Hello</p><p>World</p>");

        Assert.Contains("Hello", text);
        Assert.Contains("World", text);
        Assert.DoesNotContain("<", text);
        Assert.Contains("\n", text); // the two paragraphs are on separate lines
    }

    [Fact]
    public void StripHtml_decodes_entities()
        => Assert.Equal("A & B", TelegramText.StripHtml("A &amp; B"));

    [Fact]
    public void Render_bolds_subject_and_includes_flattened_body()
    {
        var render = TelegramLane.Render(new Notification
        {
            Subject = "Discovery: 3 new",
            HtmlBody = "<p>Northwoods Tech Summit</p>",
        });

        Assert.Contains("<b>Discovery: 3 new</b>", render);
        Assert.Contains("Northwoods Tech Summit", render);
    }

    [Fact]
    public void Render_with_empty_body_is_just_the_subject()
    {
        var render = TelegramLane.Render(new Notification { Subject = "Ping", HtmlBody = "" });
        Assert.Equal("<b>Ping</b>", render);
    }
}
