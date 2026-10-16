using System.Globalization;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Notifications;

public sealed record IncidentState(AgentRunRecord Run, bool Notify);

public sealed class RunReporter(IOperationsApiClient operations, INotifier notifier, TimeProvider time,
    IOptions<NotificationOptions>? options = null)
{
    public async Task<VersionedOperation> StartAsync(string agent, CancellationToken ct)
    {
        var run = new AgentRunRecord { RunId = Guid.NewGuid().ToString("N"), Agent = agent, StartedUtc = time.GetUtcNow() };
        return await operations.SaveAsync(OperationState.Document("runs", run.RunId, run, time.GetUtcNow()), ct: ct)
            ?? throw new InvalidOperationException("Run identity collision.");
    }

    public async Task CompleteAsync(VersionedOperation started, int considered, int changed, IReadOnlyList<RunIssue> issues,
        bool failed = false, CancellationToken ct = default, IReadOnlyDictionary<string, long>? metrics = null,
        string? modelDeployment = null)
    {
        var run = OperationState.Read<AgentRunRecord>(started)!;
        run = run with { CompletedUtc = time.GetUtcNow(), Considered = considered, Changed = changed, Issues = issues,
            Status = failed ? "Failed" : issues.Count > 0 ? "Degraded" : "Succeeded", Metrics = metrics ?? new Dictionary<string, long>(),
            ModelDeployment = modelDeployment, EstimatedCost = EstimateCost(modelDeployment, metrics, options?.Value) };
        var completed = await operations.SaveAsync(OperationState.Document("runs", run.RunId, run, time.GetUtcNow()), started.Version, ct);
        if (completed is null)
        {
            // Completion can be retried after an incident delivery/write failure. Never
            // replace a persisted outcome with the wrapper's later reporting failure.
            var persisted = OperationState.Read<AgentRunRecord>(await operations.GetAsync("runs", run.RunId, ct));
            if (persisted?.CompletedUtc is null) throw new InvalidOperationException("Run completion changed concurrently.");
            run = persisted;
            issues = run.Issues;
        }
        var id = OperationState.Key(run.Agent);
        var previous = await operations.GetAsync("incidents", id, ct);
        var priorState = OperationState.Read<IncidentState>(previous);
        var prior = priorState?.Run;
        var fingerprint = string.Join(',', issues.Select(i => $"{i.Source}:{i.Code}").Distinct().Order(StringComparer.Ordinal));
        var priorFingerprint = string.Join(',', (prior?.Issues ?? []).Select(i => $"{i.Source}:{i.Code}").Distinct().Order(StringComparer.Ordinal));
        if (fingerprint == priorFingerprint && run.Status == prior?.Status)
        {
            // Retry the original transition identity: the delivery coordinator skips
            // Sent/Uncertain parts and retries only confirmed Failed/unsent parts.
            if (priorState is { Notify: true }) await NotifyTransitionAsync(prior!, ct);
            return;
        }
        var transition = new IncidentState(run, issues.Count > 0 || prior is { Issues.Count: > 0 });
        var saved = await operations.SaveAsync(OperationState.Document("incidents", id, transition, time.GetUtcNow()), previous?.Version, ct);
        if (saved is null) return;
        if (transition.Notify) await NotifyTransitionAsync(run, ct);
    }

    private Task NotifyTransitionAsync(AgentRunRecord run, CancellationToken ct)
    {
        var detail = run.Issues.Count == 0 ? "Normal operation recovered."
            : string.Join("; ", run.Issues.GroupBy(i => (i.Source, i.Code)).Select(g => $"{g.Key.Source}: {g.Key.Code} ({g.Sum(i => i.Count)})"));
        return notifier.NotifyAsync(new Notification
        {
            Subject = $"Pipeline {run.Agent}: {run.Status.ToLowerInvariant()}", HtmlBody = System.Net.WebUtility.HtmlEncode(detail),
            DeliveryKey = $"incident-{run.Agent}-{run.RunId}",
        }, ct);
    }

    internal static decimal? EstimateCost(string? deployment, IReadOnlyDictionary<string, long>? metrics, NotificationOptions? options)
    {
        if (deployment is null || metrics is null || options is null || !options.ModelPricing.TryGetValue(deployment, out var price)
            || price.InputUsdPerMillionTokens is not >= 0 || price.CompletionUsdPerMillionTokens is not >= 0
            || !metrics.TryGetValue("input tokens", out var input) || !metrics.TryGetValue("completion tokens", out var completion)
            || metrics.GetValueOrDefault("usage incomplete") > 0) return null;
        return (input * price.InputUsdPerMillionTokens.Value + completion * price.CompletionUsdPerMillionTokens.Value) / 1_000_000m;
    }
}

public sealed class PipelineHealth(IOperationsApiClient operations, TimeProvider time)
{
    public static TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    public bool IsSilent => IsQuiet(time.GetUtcNow());
    public static string WeekKey(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, Zone);
        return $"{ISOWeek.GetYear(local.DateTime)}-{ISOWeek.GetWeekOfYear(local.DateTime)}";
    }
    public static bool IsQuiet(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, Zone);
        return local.Hour >= 22 || local.Hour < 7;
    }

    public async Task<Notification> BuildAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var runs = (await operations.ListAsync("runs", now.AddDays(-14), ct))
            .Select(OperationState.Read<AgentRunRecord>).OfType<AgentRunRecord>().ToArray();
        var rows = new List<NotificationItem>();
        foreach (var agent in new[] { "discovery", "tracker", "scoring", "catalog" })
        {
            var latest = runs.Where(r => r.Agent == agent).OrderByDescending(r => r.StartedUtc).FirstOrDefault();
            var stale = latest is null || now - latest.StartedUtc > TimeSpan.FromDays(agent == "scoring" ? 8 : 2);
            var detail = latest is null ? "No recorded runs." : $"{latest.Status}; last run {TimeZoneInfo.ConvertTime(latest.StartedUtc, Zone):MMM d HH:mm}; {latest.Considered} considered, {latest.Changed} changed.";
            if (stale) detail += " Run is overdue.";
            if (latest?.Issues.Count > 0) detail += " " + string.Join("; ", latest.Issues.Select(i => $"{i.Source}: {i.Code} ({i.Count})"));
            if (latest?.Metrics.Count > 0) detail += " " + string.Join("; ", latest.Metrics.Select(m => $"{m.Key}: {m.Value}"));
            rows.Add(new NotificationItem { Id = agent, Name = agent, Detail = detail });
        }
        var week = runs.Where(r => r.StartedUtc >= now.AddDays(-7)).ToArray();
        var totals = week.SelectMany(r => r.Metrics).GroupBy(m => m.Key, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}: {g.Sum(m => m.Value)}");
        rows.Add(new NotificationItem { Id = "weekly-totals", Name = "Last seven days",
            Detail = $"{week.Length} runs; {week.Sum(r => r.Considered)} considered; {week.Sum(r => r.Changed)} changed. " + string.Join("; ", totals) });
        var recurrence = week.SelectMany(r => r.Issues.Select(i => (Run: r, Issue: i)))
            .GroupBy(x => (x.Run.Agent, x.Issue.Source, x.Issue.Code))
            .OrderBy(g => g.Key.Agent).ThenBy(g => g.Key.Source).ThenBy(g => g.Key.Code)
            .Select(g => $"{g.Key.Agent}/{g.Key.Source}: {g.Key.Code} in {g.Select(x => x.Run.RunId).Distinct().Count()} runs ({g.Sum(x => x.Issue.Count)} occurrences)");
        rows.Add(new NotificationItem { Id = "weekly-incidents", Name = "Incident recurrence", Detail = string.Join("; ", recurrence) is { Length: > 0 } recurring ? recurring : "No recorded incidents in the last seven days." });
        var deliveries = await operations.ListAsync("deliveries", now.AddDays(-7), ct);
        var incomplete = deliveries.Select(OperationState.Read<DeliveryState>).Count(d => d?.Status is "Failed" or "Uncertain" or "Sending");
        var queued = (await operations.ListAsync("outbox", now.AddDays(-7), ct)).Select(OperationState.Read<OutboxEntry>).OfType<OutboxEntry>().ToArray();
        var modelRuns = week.Where(r => r.ModelDeployment is not null || r.Metrics.ContainsKey("input tokens")).ToArray();
        var cost = modelRuns.Length > 0 && modelRuns.All(r => r.EstimatedCost is not null)
            ? "$" + modelRuns.Sum(r => r.EstimatedCost!.Value).ToString("0.0000", CultureInfo.InvariantCulture) + " USD estimated"
            : "unknown";
        rows.Add(new NotificationItem { Id = "delivery", Name = "Delivery", Detail = $"{incomplete} incomplete or uncertain deliveries in the last seven days; {queued.Count(q => q.Status is "Ready" or "Processing")} queued, {queued.Count(q => q.Status is "Frozen" or "Exhausted")} requiring review. Estimated model cost: {cost}." });
        return new Notification { Subject = "Speaker pipeline — weekly health", HtmlBody = "", Groups = [new("Health", rows)],
            DeliveryKey = $"health-{WeekKey(now)}" };
    }
}
