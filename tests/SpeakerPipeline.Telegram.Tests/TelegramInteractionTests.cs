using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Core;
using SpeakerPipeline.Notifications;

namespace SpeakerPipeline.Telegram.Tests;

public sealed class TelegramInteractionTests
{
    [Fact]
    public void Packed_actionable_items_reserve_space_for_all_later_state_edits()
    {
        var items = Enumerable.Range(1, 80).Select(i => new NotificationItem
        { Id = $"event-{i}", EventSlug = $"event-{i}", Name = "Event", Detail = new string('x', 25), Actions = true }).ToArray();
        var parts = TelegramMessageFormatter.Parts(new Notification
        { Subject = "80 opportunities", HtmlBody = "", Groups = [new("SubmitNow", items)] });
        Assert.Equal(80, parts.Sum(p => p.Items?.Count ?? 0));
        foreach (var part in parts)
        {
            var state = new TelegramMessageState(1, 1, part.Text!, (part.Items ?? []).Select(i =>
                new TelegramMessageItem(i.Id, i.EventSlug!, i.Name, "Changed elsewhere; use /status")).ToArray(), DateTimeOffset.MaxValue);
            Assert.InRange(TelegramMessageFormatter.VisibleLength(TelegramInteractions.Render(state)), 1, 4096);
        }
    }

    [Fact]
    public void Oversized_linked_name_is_continued_without_losing_characters_or_actions()
    {
        var name = string.Concat(Enumerable.Repeat("NAME & 😀 ", 1500));
        var parts = TelegramMessageFormatter.Parts(new Notification { Subject = "Test", HtmlBody = "", Groups =
        [new("SubmitNow", [new NotificationItem { Id = "event", EventSlug = "event", Name = name,
            Url = "https://example.test/cfp", Detail = "All details retained.", Actions = true }])] });
        Assert.All(parts, p => Assert.InRange(TelegramMessageFormatter.VisibleLength(p.Text!), 1, 4096));
        var linkedName = string.Concat(parts.SelectMany(p => System.Text.RegularExpressions.Regex.Matches(p.Text!, "<a[^>]*>(.*?)</a>")
            .Select(match => System.Net.WebUtility.HtmlDecode(match.Groups[1].Value))));
        Assert.Equal(name, linkedName);
        Assert.Contains("All details retained.", parts[^1].Text);
        Assert.Single(parts.SelectMany(p => p.Items ?? []), i => i.Actions);
    }

    [Fact]
    public void Formatter_keeps_groups_whole_then_continues_oversized_items_without_losing_text()
    {
        var detail = string.Concat(Enumerable.Repeat("DATA <unsafe> & 😀 ", 900));
        var notification = new Notification { Subject = "Test", HtmlBody = "", Groups =
        [new("SubmitNow", [new NotificationItem { Id = "event", Name = "A & B", Url = "https://example.test/cfp?a=1&b=2", Detail = detail, Actions = true, EventSlug = "event" }]),
         new("Outreach", [new NotificationItem { Id = "other", Name = "Other", Detail = "Review" }])] };
        var parts = TelegramMessageFormatter.Parts(notification);
        Assert.True(parts.Count > 3);
        Assert.All(parts, p => Assert.InRange(TelegramMessageFormatter.VisibleLength(p.Text!), 1, 4096));
        Assert.All(parts, p => Assert.DoesNotContain("<unsafe>", p.Text));
        Assert.Equal(900, string.Concat(parts.Select(p => p.Text)).Split("DATA", StringSplitOptions.None).Length - 1);
        Assert.Contains("href=\"https://example.test/cfp?a=1&amp;b=2\"", parts[0].Text);
        Assert.StartsWith("<b>Test</b>", parts[0].Text);
        Assert.Contains("<b>Outreach</b>", parts[^1].Text);
    }

    [Fact]
    public async Task Submitted_callback_is_bound_and_idempotent_and_edits_original_message()
    {
        var setup = await Setup();
        await setup.Handler.HandleAsync(setup.Callback, default);
        await setup.Handler.HandleAsync(setup.Callback, default);
        Assert.Equal(1, setup.Opportunities.Actions);
        Assert.Equal(OpportunityAction.Submitted, setup.Opportunities.LastAction);
        Assert.Equal(2, setup.Transport.Methods.Count(m => m == "editMessageText"));
        Assert.Contains("Submitted", setup.Transport.Bodies.Last());
        Assert.DoesNotContain("Submitted\",\"callback_data", setup.Transport.Bodies.Last());
    }

    [Fact]
    public async Task Wrong_user_or_nonprivate_chat_cannot_mutate()
    {
        var setup = await Setup();
        await setup.Handler.HandleAsync(setup.Callback with { From = new TelegramUser { Id = 88 } }, default);
        await setup.Handler.HandleAsync(setup.Callback with { Message = setup.Callback.Message! with { Chat = new TelegramChat { Id = 7, Type = "group" } } }, default);
        Assert.Equal(0, setup.Opportunities.Actions);
        Assert.DoesNotContain("editMessageText", setup.Transport.Methods);
    }

    [Fact]
    public async Task Old_buttons_cannot_overwrite_a_newer_dashboard_revision_or_decision()
    {
        var setup = await Setup();
        setup.Opportunities.CurrentETag = "2";
        await setup.Handler.HandleAsync(setup.Callback, default);
        Assert.Equal(0, setup.Opportunities.Actions);
        Assert.Contains(setup.Transport.Bodies, body => body.Contains("Changed elsewhere; use /status", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Drop_requires_reason_picker_before_mutating()
    {
        var setup = await Setup();
        var data = setup.Callback.Data![..^1];
        await setup.Handler.HandleAsync(setup.Callback with { Data = data + "d" }, default);
        Assert.Equal(0, setup.Opportunities.Actions);
        await setup.Handler.HandleAsync(setup.Callback with { Data = data + "r0" }, default);
        Assert.Equal(OpportunityAction.Drop, setup.Opportunities.LastAction);
        Assert.Equal(FeedbackReason.WrongAudience, setup.Opportunities.LastReason);
    }

    [Fact]
    public async Task Mutation_survives_failed_edit_and_replay_only_repairs_message()
    {
        var setup = await Setup();
        setup.Transport.FailEdit = true;
        await Assert.ThrowsAsync<TelegramApiException>(() => setup.Handler.HandleAsync(setup.Callback, default));
        setup.Transport.FailEdit = false;
        await setup.Handler.HandleAsync(setup.Callback, default);
        Assert.Equal(1, setup.Opportunities.Actions);
    }

    [Fact]
    public async Task Saved_action_edit_repair_refreshes_later_owner_placement_without_repeating_the_command()
    {
        var setup = await Setup();
        setup.Transport.FailEdit = true;
        await Assert.ThrowsAsync<TelegramApiException>(() => setup.Handler.HandleAsync(setup.Callback, default));
        setup.Opportunities.MoveElsewhere(OpportunityStatus.Accepted);
        setup.Transport.FailEdit = false;

        await setup.Handler.HandleAsync(setup.Callback, default);

        Assert.Equal(1, setup.Opportunities.Actions);
        using var edit = JsonDocument.Parse(setup.Transport.Bodies.Last());
        Assert.Contains("Item 1: Accepted", edit.RootElement.GetProperty("text").GetString());
        Assert.DoesNotContain("Item 1: Submitted", edit.RootElement.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("s", OpportunityAction.Submitted)]
    [InlineData("z", OpportunityAction.Snooze7Days)]
    public async Task Expired_acknowledgement_does_not_block_saved_action_edit_repair(string action, OpportunityAction expected)
    {
        var setup = await Setup();
        var callback = setup.Callback with { Data = setup.Callback.Data![..^1] + action };
        setup.Transport.FailEdit = true;
        await Assert.ThrowsAsync<TelegramApiException>(() => setup.Handler.HandleAsync(callback, default));
        var originalExpiry = setup.Opportunities.SnoozedUntilUtc;
        setup.Transport.FailEdit = false;
        setup.Transport.FailAnswer = true;
        await setup.Handler.HandleAsync(callback, default);
        Assert.Equal(1, setup.Opportunities.Actions);
        Assert.Equal(expected, setup.Opportunities.LastAction);
        Assert.Equal(originalExpiry, setup.Opportunities.SnoozedUntilUtc);
        Assert.Equal(2, setup.Transport.Methods.Count(m => m == "editMessageText"));
        Assert.Contains(expected == OpportunityAction.Submitted ? "Submitted" : "Snoozed 7 days", setup.Transport.Bodies.Last());
        if (expected == OpportunityAction.Snooze7Days) Assert.NotNull(originalExpiry);
    }

    [Fact]
    public async Task Cancellation_and_failed_unauthorized_acknowledgements_never_apply_an_action()
    {
        var setup = await Setup();
        setup.Transport.CancelAnswer = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.Handler.HandleAsync(setup.Callback, default));
        Assert.Equal(0, setup.Opportunities.Actions);
        setup.Transport.CancelAnswer = false;
        setup.Transport.FailAnswer = true;
        await Assert.ThrowsAsync<TelegramApiException>(() => setup.Handler.HandleAsync(setup.Callback with { From = new TelegramUser { Id = 88 } }, default));
        Assert.Equal(0, setup.Opportunities.Actions);
        Assert.DoesNotContain("editMessageText", setup.Transport.Methods);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actionable_html_is_stored_in_bounded_chunks_and_edited_losslessly(bool surrogateBoundary)
    {
        var body = surrogateBoundary ? new string('a', 3499) + "😀" + string.Concat(Enumerable.Repeat("&amp;", 450))
            : string.Concat(Enumerable.Repeat("&amp;", 3500));
        var text = body + "\n<i>Item 1: Awaiting action</i>";
        Assert.True(TelegramMessageFormatter.VisibleLength(text) <= 4096);
        if (!surrogateBoundary) Assert.True(JsonSerializer.Serialize(new TelegramMessageState(7, 0, text, [], DateTimeOffset.UtcNow)).Length > 30000);
        var setup = await Setup(text);
        var message = OperationState.Read<TelegramMessageState>(await setup.Store.GetAsync("messages", "message"))!;
        Assert.Empty(message.Text);
        Assert.True(message.TextChunks!.Count > 1);
        var storedText = new StringBuilder();
        foreach (var id in message.TextChunks)
            storedText.Append(OperationState.Read<TelegramMessageTextChunk>(await setup.Store.GetAsync("messagechunks", id))!.Text);
        Assert.Equal(text, storedText.ToString());
        await setup.Handler.HandleAsync(setup.Callback, default);
        using var edit = JsonDocument.Parse(setup.Transport.Bodies.Last());
        Assert.Equal(text.Replace("Awaiting action", "Submitted", StringComparison.Ordinal), edit.RootElement.GetProperty("text").GetString());
        Assert.Empty(edit.RootElement.GetProperty("reply_markup").GetProperty("inline_keyboard").EnumerateArray());
        Assert.Equal(1, setup.Opportunities.Actions);
    }

    private static async Task<(TelegramInteractions Handler, TelegramCallbackQuery Callback, FakeOpportunities Opportunities, Transport Transport, Operations Store)> Setup(string? text = null)
    {
        var transport = new Transport();
        var client = new TelegramClient(new HttpClient(transport), Options.Create(new TelegramOptions { Enabled = true, ChatId = 7, BotToken = "TEST_TOKEN" }), NullLogger<TelegramClient>.Instance);
        var opportunities = new FakeOpportunities();
        var store = new Operations();
        var handler = new TelegramInteractions(store, opportunities, client,
            Options.Create(new TelegramOptions { Enabled = true, ChatId = 7 }), TimeProvider.System, NullLogger<TelegramInteractions>.Instance,
            Options.Create(new PipelineNotificationOptions { OpportunitiesEnabled = true }));
        var keyboard = await handler.PrepareAsync("message", new NotificationPart("0", new Notification { Subject = "Test", HtmlBody = "" },
            text ?? "Event\n<i>Item 1: Awaiting action</i>", [new NotificationItem { Id = "event", EventSlug = "event", Name = "1. Event", Detail = "", Actions = true }]), default);
        await handler.BindAsync("message", new TelegramSentMessage(42, new TelegramChat { Id = 7 }), default);
        return (handler, new TelegramCallbackQuery { Id = "callback", From = new TelegramUser { Id = 7 },
            Message = new TelegramMessage { MessageId = 42, Chat = new TelegramChat { Id = 7, Type = "private" } }, Data = keyboard!.InlineKeyboard[0][0].CallbackData }, opportunities, transport, store);
    }

    private sealed class Transport : HttpMessageHandler
    {
        public bool FailEdit { get; set; }
        public bool FailAnswer { get; set; }
        public bool CancelAnswer { get; set; }
        public List<string> Methods { get; } = [];
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var method = request.RequestUri!.Segments.Last();
            Methods.Add(method); Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (CancelAnswer && method == "answerCallbackQuery") throw new OperationCanceledException(cancellationToken);
            var fail = FailEdit && method == "editMessageText" || FailAnswer && method == "answerCallbackQuery";
            return new HttpResponseMessage(fail ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
            { Content = new StringContent(fail ? "{\"ok\":false}" : "{\"ok\":true,\"result\":true}", Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Operations : IOperationsApiClient
    {
        private readonly Dictionary<string, VersionedOperation> _rows = [];
        private int _version;
        public Task<VersionedOperation?> GetAsync(string kind, string id, CancellationToken ct = default) => Task.FromResult(_rows.GetValueOrDefault(kind + id));
        public Task<VersionedOperation?> SaveAsync(OperationDocument document, string? expectedVersion = null, CancellationToken ct = default)
        {
            Assert.True(document.Payload.Length <= 30000, "Stored operation must fit the API/Table string limit.");
            Assert.True(Encoding.UTF8.GetByteCount(document.Payload) <= 60000);
            if (_rows.GetValueOrDefault(document.Kind + document.Id)?.Version != expectedVersion) return Task.FromResult<VersionedOperation?>(null);
            var saved = new VersionedOperation(document, (++_version).ToString()); _rows[document.Kind + document.Id] = saved;
            return Task.FromResult<VersionedOperation?>(saved);
        }
        public Task<IReadOnlyList<VersionedOperation>> ListAsync(string kind, DateTimeOffset since, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<VersionedOperation>>([]);
    }

    private sealed class FakeOpportunities : IOpportunityApiClient
    {
        public string CurrentETag { get; set; } = "1";
        public int Actions { get; private set; }
        public OpportunityAction LastAction { get; private set; }
        public FeedbackReason? LastReason { get; private set; }
        public DateTimeOffset? SnoozedUntilUtc { get; private set; }
        private OpportunityRecord _record = new() { Event = new EventRecord { Slug = "event", Name = "Event", EventType = EventType.Conference, Category = EventCategory.SubmitNow, Priority = Priority.High }, ETag = "1" };
        public void MoveElsewhere(OpportunityStatus status) => _record = _record with { UserStatus = status, ETag = "later" };
        public Task<OpportunityRecord?> GetOpportunityAsync(string slug, CancellationToken ct = default) => Task.FromResult<OpportunityRecord?>(_record);
        public Task<OpportunityRecord> ApplyOpportunityActionAsync(string slug, OpportunityActionRequest request, string expectedETag, CancellationToken ct = default)
        {
            if (expectedETag != CurrentETag) throw new OpportunityConflictException("newer revision");
            Actions++; LastAction = request.Action; LastReason = request.ReasonCode;
            _record = OpportunityTransitions.Apply(_record, request, DateTimeOffset.UtcNow);
            SnoozedUntilUtc = _record.SnoozedUntilUtc;
            return Task.FromResult(_record);
        }
        public Task<IReadOnlyList<OpportunityRecord>> GetOpportunitiesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<OpportunityRecord>>([_record]);
        public Task<IReadOnlyList<OpportunityHistoryEntry>> GetOpportunityHistoryAsync(string slug, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<OpportunityHistoryEntry>>([]);
        public Task<IReadOnlyList<OpportunityHistoryEntry>> GetOpportunityFeedbackAsync(int limit = 20, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<OpportunityHistoryEntry>>([]);
        public Task<OpportunityRecord> PostOpportunityDecisionAsync(OpportunityScoringRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
