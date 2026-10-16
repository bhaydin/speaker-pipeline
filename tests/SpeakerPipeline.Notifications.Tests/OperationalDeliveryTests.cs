using System.Net;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Notifications.Tests;

public sealed class OperationalDeliveryTests
{
    [Fact]
    public async Task Concurrent_attempts_claim_only_one_send_and_replay_known_success()
    {
        var store = new MemoryOperations();
        var coordinator = new DeliveryCoordinator(store, TimeProvider.System);
        var started = new TaskCompletionSource();
        var finish = new TaskCompletionSource();
        var sends = 0;
        async Task<NotificationDelivery> Send() { sends++; started.SetResult(); await finish.Task; return new("Sent", ["42"]); }
        var first = coordinator.DeliverAsync("run:Telegram:0", Send, default);
        await started.Task;
        Assert.Equal("Busy", (await coordinator.DeliverAsync("run:Telegram:0", Send, default)).Status);
        finish.SetResult();
        Assert.Equal("Sent", (await first).Status);
        var replay = await coordinator.DeliverAsync("run:Telegram:0", Send, default);
        Assert.Equal("42", Assert.Single(replay.MessageIds!));
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task Failed_channel_part_can_retry_without_resending_successful_parts()
    {
        var coordinator = new DeliveryCoordinator(new MemoryOperations(), TimeProvider.System);
        var attempts = 0;
        Task<NotificationDelivery> Send() { attempts++; if (attempts == 1) throw new HttpRequestException("test", null, HttpStatusCode.ServiceUnavailable); return Task.FromResult(new NotificationDelivery("Sent")); }
        Assert.Equal("Failed", (await coordinator.DeliverAsync("Telegram:1", Send, default)).Status);
        Assert.Equal("Sent", (await coordinator.DeliverAsync("Telegram:1", Send, default)).Status);
        Assert.Equal("Sent", (await coordinator.DeliverAsync("Telegram:1", Send, default)).Status);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Ambiguous_timeout_is_not_retried()
    {
        var coordinator = new DeliveryCoordinator(new MemoryOperations(), TimeProvider.System);
        var attempts = 0;
        Task<NotificationDelivery> Send() { attempts++; throw new TaskCanceledException(); }
        Assert.Equal("Uncertain", (await coordinator.DeliverAsync("Telegram:0", Send, default)).Status);
        Assert.Equal("Uncertain", (await coordinator.DeliverAsync("Telegram:0", Send, default)).Status);
        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData("2026-10-17T02:59:00Z", false)]
    [InlineData("2026-10-17T03:00:00Z", true)]
    [InlineData("2026-10-17T12:00:00Z", false)]
    [InlineData("2026-11-01T07:30:00Z", true)]
    [InlineData("2027-03-14T08:30:00Z", true)]
    public void Quiet_hours_follow_Chicago_including_DST(string utc, bool expected)
        => Assert.Equal(expected, PipelineHealth.IsQuiet(DateTimeOffset.Parse(utc)));

    [Fact]
    public async Task Incident_deduplication_and_recovery_do_not_silence_first_failure()
    {
        var notifications = new CaptureNotifier();
        var reporter = new RunReporter(new MemoryOperations(), notifications, TimeProvider.System);
        foreach (var issues in new IReadOnlyList<RunIssue>[] { [], [new("http_403", "search", 10)], [new("http_403", "search", 10)], [] })
        {
            var run = await reporter.StartAsync("discovery", default);
            await reporter.CompleteAsync(run, 0, 0, issues);
        }
        Assert.Equal(2, notifications.Notifications.Count);
        Assert.Contains("degraded", notifications.Notifications[0].Subject);
        Assert.Contains("recovered", notifications.Notifications[1].HtmlBody);
    }

    [Fact]
    public async Task Repeated_incident_retries_a_confirmed_failed_transition_with_the_same_identity()
    {
        var notifications = new CaptureNotifier(failFirst: true);
        var reporter = new RunReporter(new MemoryOperations(), notifications, TimeProvider.System);
        for (var i = 0; i < 3; i++)
            await reporter.CompleteAsync(await reporter.StartAsync("discovery", default), 0, 0, [new("http_503", "search", i + 1)]);
        Assert.Single(notifications.Notifications);
        Assert.Equal(2, notifications.Attempts);
    }

    [Fact]
    public async Task Repeated_incident_does_not_retry_an_uncertain_transition()
    {
        var notifications = new CaptureNotifier(uncertainFirst: true);
        var reporter = new RunReporter(new MemoryOperations(), notifications, TimeProvider.System);
        for (var i = 0; i < 3; i++)
            await reporter.CompleteAsync(await reporter.StartAsync("discovery", default), 0, 0, [new("http_503", "search")]);
        Assert.Empty(notifications.Notifications);
        Assert.Equal(1, notifications.Attempts);
    }

    [Fact]
    public async Task Completion_write_failure_does_not_commit_an_incident_or_send_before_retry()
    {
        var store = new MemoryOperations();
        var notifications = new CaptureNotifier();
        var reporter = new RunReporter(store, notifications, TimeProvider.System);
        var run = await reporter.StartAsync("discovery", default);
        store.FailNextCompletion = true;
        await Assert.ThrowsAsync<IOException>(() => reporter.CompleteAsync(run, 1, 0, [new("http_503", "search")]));
        Assert.Empty(notifications.Notifications);
        Assert.Empty(await store.ListAsync("incidents", DateTimeOffset.MinValue));
        await reporter.CompleteAsync(run, 1, 0, [new("http_503", "search")]);
        Assert.Single(notifications.Notifications);
        // Re-entering from a reporting catch must not rewrite a persisted outcome.
        await reporter.CompleteAsync(run, 0, 0, [new("run_failed", "discovery")], failed: true);
        var saved = OperationState.Read<AgentRunRecord>(await store.GetAsync("runs", run.Document.Id));
        Assert.Equal("Degraded", saved!.Status);
        Assert.Equal("http_503", Assert.Single(saved.Issues).Code);
    }

    private sealed class CaptureNotifier(bool failFirst = false, bool uncertainFirst = false) : INotifier
    {
        private readonly DeliveryCoordinator _coordinator = new(new MemoryOperations(), TimeProvider.System);
        public int Attempts { get; private set; }
        public List<Notification> Notifications { get; } = [];
        public async Task NotifyAsync(Notification notification, CancellationToken ct = default)
            => await _coordinator.DeliverAsync(notification.DeliveryKey!, () =>
            {
                Attempts++;
                if (Attempts == 1 && failFirst) throw new HttpRequestException("synthetic", null, HttpStatusCode.ServiceUnavailable);
                if (Attempts == 1 && uncertainFirst) throw new HttpRequestException("synthetic");
                Notifications.Add(notification);
                return Task.FromResult(new NotificationDelivery("Sent"));
            }, ct);
    }
}

internal sealed class MemoryOperations : IOperationsApiClient
{
    public bool FailNextCompletion { get; set; }
    public Func<OperationDocument, bool>? RejectWrite { get; set; }
    private readonly Dictionary<string, VersionedOperation> _documents = [];
    private int _version;
    public Task<VersionedOperation?> GetAsync(string kind, string id, CancellationToken ct = default)
    { lock (_documents) return Task.FromResult(_documents.GetValueOrDefault(kind + id)); }
    public Task<VersionedOperation?> SaveAsync(OperationDocument document, string? expectedVersion = null, CancellationToken ct = default)
    {
        lock (_documents)
        {
            if (RejectWrite?.Invoke(document) == true) throw new IOException("synthetic write failure");
            if (FailNextCompletion && document.Kind == "runs" && System.Text.Json.JsonSerializer.Deserialize<AgentRunRecord>(document.Payload)?.CompletedUtc is not null)
            { FailNextCompletion = false; throw new IOException("synthetic write failure"); }
            var key = document.Kind + document.Id;
            var existing = _documents.GetValueOrDefault(key);
            if (existing?.Version != expectedVersion) return Task.FromResult<VersionedOperation?>(null);
            var saved = new VersionedOperation(document, (++_version).ToString());
            _documents[key] = saved;
            return Task.FromResult<VersionedOperation?>(saved);
        }
    }
    public Task<IReadOnlyList<VersionedOperation>> ListAsync(string kind, DateTimeOffset since, CancellationToken ct = default)
    { lock (_documents) return Task.FromResult<IReadOnlyList<VersionedOperation>>(_documents.Values.Where(d => d.Document.Kind == kind && d.Document.UpdatedUtc >= since).ToArray()); }
}
