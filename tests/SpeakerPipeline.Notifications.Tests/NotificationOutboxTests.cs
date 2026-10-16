using System.Net;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Notifications.Tests;

public sealed class NotificationOutboxTests
{
    [Fact]
    public async Task Worker_retries_only_the_failed_original_channel_part_and_honors_disabled_lanes()
    {
        var store = new MemoryOperations();
        var clock = new Clock();
        var outbox = new NotificationOutbox(store, new DeliveryCoordinator(store, clock), clock);
        var email = new Lane(NotificationChannel.Email);
        var telegram = new Lane(NotificationChannel.Telegram) { FailPartOnce = "1" };
        await outbox.DeliverAsync("run:Email:0", email, Part("0", "email"), default);
        await outbox.DeliverAsync("run:Telegram:0", telegram, Part("0", "first"), default);
        await outbox.DeliverAsync("run:Telegram:1", telegram, Part("1", "original second"), default);
        clock.Advance(); telegram.Enabled = false;
        Assert.Equal(0, await outbox.RetryAsync([email, telegram], default));
        telegram.Enabled = true;
        Assert.Equal(1, await outbox.RetryAsync([email, telegram], default));
        Assert.Single(email.Attempts);
        Assert.Equal(new[] { "first", "original second", "original second" }, telegram.Attempts.Select(p => p.Text));
        Assert.Equal(0, await outbox.RetryAsync([email, telegram], default));
    }

    [Fact]
    public async Task Uncertain_sends_stay_frozen_in_the_outbox()
    {
        var store = new MemoryOperations(); var clock = new Clock();
        var outbox = new NotificationOutbox(store, new DeliveryCoordinator(store, clock), clock);
        var lane = new Lane(NotificationChannel.Telegram) { Uncertain = true };
        Assert.Equal("Uncertain", (await outbox.DeliverAsync("key", lane, Part("0", "text"), default)).Status);
        clock.Advance();
        Assert.Equal(0, await outbox.RetryAsync([lane], default));
        Assert.Single(lane.Attempts);
        Assert.Equal("Frozen", OperationState.Read<OutboxEntry>(Assert.Single(await store.ListAsync("outbox", DateTimeOffset.MinValue)))!.Status);
    }

    [Fact]
    public async Task Twenty_paused_parts_cannot_starve_an_enabled_channel_retry()
    {
        var store = new MemoryOperations(); var clock = new Clock();
        var outbox = new NotificationOutbox(store, new DeliveryCoordinator(store, clock), clock);
        var email = new Lane(NotificationChannel.Email) { AlwaysFail = true };
        var telegram = new Lane(NotificationChannel.Telegram) { FailPartOnce = "0" };
        for (var i = 0; i < 20; i++)
            await outbox.DeliverAsync($"email:{i}", email, Part(i.ToString(), "paused email"), default);
        clock.Advance();
        await outbox.DeliverAsync("telegram:0", telegram, Part("0", "retry telegram"), default);
        clock.Advance(); email.Enabled = false;
        Assert.Equal(1, await outbox.RetryAsync([email, telegram], default));
        Assert.Equal(20, email.Attempts.Count);
        Assert.Equal(2, telegram.Attempts.Count);
    }

    [Fact]
    public async Task Known_sent_receipt_repairs_interaction_binding_without_sending_again()
    {
        var store = new MemoryOperations(); var clock = new Clock();
        var outbox = new NotificationOutbox(store, new DeliveryCoordinator(store, clock), clock);
        var lane = new Lane(NotificationChannel.Telegram) { FailRepairOnce = true };
        await outbox.DeliverAsync("key", lane, Part("0", "text"), default);
        clock.Advance();
        await outbox.RetryAsync([lane], default);
        Assert.Single(lane.Attempts);
        Assert.Equal(2, lane.Repairs);
        Assert.Equal("Completed", OperationState.Read<OutboxEntry>(Assert.Single(await store.ListAsync("outbox", DateTimeOffset.MinValue)))!.Status);
    }

    [Fact]
    public async Task Receipt_write_failure_after_send_freezes_instead_of_resending()
    {
        var store = new MemoryOperations(); var clock = new Clock();
        var outbox = new NotificationOutbox(store, new DeliveryCoordinator(store, clock), clock);
        var lane = new Lane(NotificationChannel.Telegram);
        store.RejectWrite = d => d.Kind == "deliveries" && System.Text.Json.JsonSerializer.Deserialize<DeliveryState>(d.Payload)?.Status == "Sent";
        await Assert.ThrowsAsync<IOException>(() => outbox.DeliverAsync("key", lane, Part("0", "text"), default));
        store.RejectWrite = null; clock.Advance();
        await outbox.RetryAsync([lane], default);
        Assert.Single(lane.Attempts);
        Assert.Equal("Frozen", OperationState.Read<OutboxEntry>(Assert.Single(await store.ListAsync("outbox", DateTimeOffset.MinValue)))!.Status);
    }

    [Fact]
    public async Task Outbox_payload_is_committed_before_any_external_send()
    {
        var store = new MemoryOperations { RejectWrite = d => d.Kind == "outbox" };
        var clock = new Clock(); var outbox = new NotificationOutbox(store, new DeliveryCoordinator(store, clock), clock);
        var lane = new Lane(NotificationChannel.Telegram);
        await Assert.ThrowsAsync<IOException>(() => outbox.DeliverAsync("key", lane, Part("0", "text"), default));
        Assert.Empty(lane.Attempts);
    }

    [Fact]
    public async Task Large_original_parts_are_chunked_below_API_payload_limits_and_reconstructed_exactly()
    {
        var store = new MemoryOperations(); var clock = new Clock();
        var outbox = new NotificationOutbox(store, new DeliveryCoordinator(store, clock), clock);
        var lane = new Lane(NotificationChannel.Email) { FailPartOnce = "0" };
        var original = string.Concat(Enumerable.Repeat("Body & 😀 <characters> ", 3000));
        var part = new NotificationPart("0", new Notification { Subject = "Subject", HtmlBody = original });
        await outbox.DeliverAsync("key", lane, part, default);
        clock.Advance(); await outbox.RetryAsync([lane], default);
        Assert.Equal(original, lane.Attempts.Last().Notification.HtmlBody);
        var chunks = await store.ListAsync("outboxchunks", DateTimeOffset.MinValue);
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => { Assert.True(c.Document.Payload.Length <= 30000); Assert.True(System.Text.Encoding.UTF8.GetByteCount(c.Document.Payload) <= 60000); });
    }

    [Fact]
    public async Task Concurrent_workers_share_the_delivery_claim()
    {
        var store = new MemoryOperations(); var clock = new Clock();
        var outbox = new NotificationOutbox(store, new DeliveryCoordinator(store, clock), clock);
        var lane = new Lane(NotificationChannel.Telegram) { FailPartOnce = "0" };
        await outbox.DeliverAsync("key", lane, Part("0", "text"), default);
        clock.Advance();
        lane.Started = new TaskCompletionSource(); lane.Finish = new TaskCompletionSource();
        var first = outbox.RetryAsync([lane], default);
        await lane.Started.Task;
        Assert.Equal(0, await outbox.RetryAsync([lane], default));
        lane.Finish.SetResult(); await first;
        Assert.Equal(2, lane.Attempts.Count);
    }

    [Fact]
    public async Task Worker_stops_after_five_confirmed_failures()
    {
        var store = new MemoryOperations(); var clock = new Clock();
        var outbox = new NotificationOutbox(store, new DeliveryCoordinator(store, clock), clock);
        var lane = new Lane(NotificationChannel.Telegram) { AlwaysFail = true };
        await outbox.DeliverAsync("key", lane, Part("0", "text"), default);
        for (var i = 0; i < 8; i++) { clock.Advance(); await outbox.RetryAsync([lane], default); }
        Assert.Equal(5, lane.Attempts.Count);
        Assert.Equal("Exhausted", OperationState.Read<OutboxEntry>(Assert.Single(await store.ListAsync("outbox", DateTimeOffset.MinValue)))!.Status);
    }

    private static NotificationPart Part(string id, string text) => new(id, new Notification { Subject = "Original subject", HtmlBody = "" }, text);
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-10-19T13:00:00Z");
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance() => _now = _now.AddHours(2);
    }
    private sealed class Lane(NotificationChannel channel) : INotificationLane
    {
        public NotificationChannel Channel => channel;
        public bool Enabled { get; set; } = true;
        public bool IsEnabled => Enabled;
        public bool Uncertain { get; set; }
        public bool AlwaysFail { get; set; }
        public bool FailRepairOnce { get; set; }
        public string? FailPartOnce { get; set; }
        public int Repairs { get; private set; }
        public TaskCompletionSource? Started { get; set; }
        public TaskCompletionSource? Finish { get; set; }
        public List<NotificationPart> Attempts { get; } = [];
        public Task SendAsync(Notification notification, CancellationToken ct = default) => throw new NotSupportedException();
        public async Task<NotificationDelivery> SendPartAsync(NotificationPart part, CancellationToken ct = default)
        {
            Attempts.Add(part);
            if (Uncertain) throw new HttpRequestException("synthetic unknown send outcome");
            if (AlwaysFail || part.Id == FailPartOnce) { FailPartOnce = null; throw new HttpRequestException("synthetic rejection", null, HttpStatusCode.ServiceUnavailable); }
            Started?.TrySetResult();
            if (Finish is not null) await Finish.Task;
            return new("Sent", ["42"]);
        }
        public Task ReconcileReceiptAsync(NotificationPart part, NotificationDelivery delivery, CancellationToken ct = default)
        {
            Repairs++;
            if (FailRepairOnce) { FailRepairOnce = false; throw new IOException("synthetic binding failure"); }
            return Task.CompletedTask;
        }
    }
}
