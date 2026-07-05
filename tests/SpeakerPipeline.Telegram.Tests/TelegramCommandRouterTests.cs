using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Telegram.Tests;

public class TelegramCommandRouterTests
{
    private const long AllowedChat = 100;

    private static TelegramCommandRouter Router(FakeApiClient api) => new(
        api,
        Options.Create(new TelegramOptions { Enabled = true, BotToken = "t", ChatId = AllowedChat, WebhookSecret = "s" }),
        NullLogger<TelegramCommandRouter>.Instance);

    private static TelegramUpdate Msg(string text, long chatId = AllowedChat) =>
        new() { Message = new TelegramMessage { Text = text, Chat = new TelegramChat { Id = chatId } } };

    private static EventRecord Event(string slug, string name, EventCategory category, DateTimeOffset? deadline = null) => new()
    {
        Slug = slug,
        Name = name,
        EventType = EventType.Conference,
        Category = category,
        Priority = Priority.Medium,
        CfpDeadline = deadline,
    };

    [Fact]
    public async Task Status_reports_counts_and_lists_actionable_slugs()
    {
        var api = new FakeApiClient();
        api.Events["northwoods"] = Event("northwoods", "Northwoods Tech Summit", EventCategory.SubmitNow, DateTimeOffset.UtcNow.AddDays(12));
        api.Events["driftless"] = Event("driftless", "Driftless AI Days", EventCategory.Outreach);
        api.Events["past"] = Event("past", "Past Event", EventCategory.Delivered);

        var reply = await Router(api).RouteAsync(Msg("/status"));

        Assert.NotNull(reply);
        Assert.Equal(AllowedChat, reply!.ChatId);
        Assert.Contains("Pipeline", reply.Text);
        Assert.Contains("northwoods", reply.Text);      // slug is shown so /approve is usable
        Assert.Contains("Driftless AI Days", reply.Text);
    }

    [Fact]
    public async Task Topic_queues_an_idea_sourced_from_telegram()
    {
        var api = new FakeApiClient();

        var reply = await Router(api).RouteAsync(Msg("/topic  Agentic RAG eval patterns! "));

        var topic = Assert.Single(api.UpsertedTopics);
        Assert.Equal("agentic-rag-eval-patterns", topic.TopicId);
        Assert.Equal("Agentic RAG eval patterns!", topic.Title);
        Assert.Equal(TopicStage.Idea, topic.Stage);
        Assert.Equal(TopicSource.Telegram, topic.Source);
        Assert.Contains("queued", reply!.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Topic_without_argument_returns_usage_and_writes_nothing()
    {
        var api = new FakeApiClient();

        var reply = await Router(api).RouteAsync(Msg("/topic"));

        Assert.Empty(api.UpsertedTopics);
        Assert.Contains("Usage", reply!.Text);
    }

    [Fact]
    public async Task Approve_applies_intend_to_the_event()
    {
        var api = new FakeApiClient();
        api.Events["northwoods"] = Event("northwoods", "Northwoods Tech Summit", EventCategory.SubmitNow);

        var reply = await Router(api).RouteAsync(Msg("/approve northwoods"));

        var (slug, request) = Assert.Single(api.Actions);
        Assert.Equal("northwoods", slug);
        Assert.Equal(PipelineAction.Intend, request.Action);
        Assert.Contains("Northwoods Tech Summit", reply!.Text);
    }

    [Fact]
    public async Task Pass_applies_skip_to_the_event()
    {
        var api = new FakeApiClient();
        api.Events["driftless"] = Event("driftless", "Driftless AI Days", EventCategory.Monitor);

        await Router(api).RouteAsync(Msg("/pass driftless"));

        var (slug, request) = Assert.Single(api.Actions);
        Assert.Equal("driftless", slug);
        Assert.Equal(PipelineAction.Skip, request.Action);
    }

    [Fact]
    public async Task Approve_unknown_slug_returns_friendly_error()
    {
        var api = new FakeApiClient();

        var reply = await Router(api).RouteAsync(Msg("/approve does-not-exist"));

        Assert.Contains("Couldn't apply", reply!.Text);
    }

    [Fact]
    public async Task Message_from_other_chat_is_rejected_and_writes_nothing()
    {
        var api = new FakeApiClient();
        api.Events["northwoods"] = Event("northwoods", "Northwoods", EventCategory.SubmitNow);

        var reply = await Router(api).RouteAsync(Msg("/approve northwoods", chatId: 999));

        Assert.Equal("Not authorized.", reply!.Text);
        Assert.Empty(api.Actions);
        Assert.Empty(api.UpsertedTopics);
    }

    [Fact]
    public async Task Help_lists_the_commands()
    {
        var reply = await Router(new FakeApiClient()).RouteAsync(Msg("/help"));
        Assert.Contains("/status", reply!.Text);
        Assert.Contains("/topic", reply.Text);
        Assert.Contains("/approve", reply.Text);
    }

    [Fact]
    public async Task Unknown_command_falls_back_to_help()
    {
        var reply = await Router(new FakeApiClient()).RouteAsync(Msg("/wat"));
        Assert.Contains("Unknown command", reply!.Text);
    }

    [Fact]
    public async Task Update_without_a_message_is_ignored()
    {
        var reply = await Router(new FakeApiClient()).RouteAsync(new TelegramUpdate { UpdateId = 1 });
        Assert.Null(reply);
    }

    [Theory]
    [InlineData("/status", "/status", "")]
    [InlineData("/topic hello world", "/topic", "hello world")]
    [InlineData("/status@SpeakerPipelineBot", "/status", "")]
    [InlineData("/APPROVE  northwoods ", "/approve", "northwoods")]
    public void Parse_splits_command_and_strips_bot_suffix(string input, string command, string argument)
    {
        var (c, a) = TelegramCommandRouter.Parse(input);
        Assert.Equal(command, c);
        Assert.Equal(argument, a);
    }

    [Fact]
    public void FormatDeadline_handles_null_and_past()
    {
        Assert.Equal("no deadline", TelegramCommandRouter.FormatDeadline(null));
        Assert.StartsWith("closed", TelegramCommandRouter.FormatDeadline(DateTimeOffset.UtcNow.AddDays(-5)));
    }
}
