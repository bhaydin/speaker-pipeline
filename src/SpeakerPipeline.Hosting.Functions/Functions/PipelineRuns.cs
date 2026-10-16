using SpeakerPipeline.Agents.Discovery;
using SpeakerPipeline.Agents.Scoring;
using SpeakerPipeline.Agents.TrackerMaintenance;
using SpeakerPipeline.Core;
using SpeakerPipeline.Notifications;
using Microsoft.Extensions.Options;

namespace SpeakerPipeline.Hosting.Functions.Functions;

/// <summary>Shared execution path for timer and manual invocations, including durable run outcomes.</summary>
public sealed class PipelineRuns(DiscoveryAgent discovery, TrackerMaintenanceAgent tracker, ScoringAgent scoring,
    INotifier notifier, RunReporter reporter, IOpportunityApiClient opportunities, ISpeakerPipelineApiClient api, TimeProvider time,
    IOptions<PipelineNotificationOptions> pipeline, IOptions<DiscoveryOptions> discoveryOptions,
    IOptions<ScoringAgentOptions> scoringOptions)
{
    public async Task<DiscoveryRunReport> DiscoveryAsync(CancellationToken ct)
    {
        var run = await reporter.StartAsync("discovery", ct);
        try
        {
            var result = await discovery.RunAsync(ct);
            var f = result.Funnel;
            var metrics = new Dictionary<string, long>
            {
                ["candidates"] = f.Targets, ["extracted"] = f.Extracted, ["passed floor"] = f.PassedFloor,
                ["new"] = f.New, ["updated"] = f.Updated, ["quarantined"] = f.Quarantined,
                ["tokens"] = f.TotalTokens, ["search queries"] = f.SearchQueries,
                ["input tokens"] = f.InputTokens, ["completion tokens"] = f.OutputTokens, ["usage incomplete"] = f.UsageComplete ? 0 : 1,
            };
            foreach (var source in result.SourceCoverage ?? f.CandidatesBySource) metrics["source " + source.Key] = source.Value;
            foreach (var dropped in f.Dropped) metrics["dropped " + dropped.Key] = dropped.Value;
            await reporter.CompleteAsync(run, f.Targets, result.Changed.Count + result.Quarantined.Count, result.Issues ?? [], ct: ct, metrics: metrics,
                modelDeployment: discoveryOptions.Value.ModelName);
            if (result.Changed.Count + result.Quarantined.Count > 0)
            {
                var events = (await api.GetEventsAsync(ct: ct)).ToDictionary(e => e.Slug);
                var state = pipeline.Value.OpportunitiesEnabled
                    ? (await opportunities.GetOpportunitiesAsync(ct)).ToDictionary(o => o.Slug)
                    : new Dictionary<string, OpportunityRecord>();
                bool Visible(DiscoveryResult item) => !state.TryGetValue(item.Slug, out var opportunity)
                    || (!opportunity.IsSnoozed(time.GetUtcNow()) && !opportunity.DoNotResurface);
                NotificationItem Item(DiscoveryResult item) => new()
                {
                    Id = item.Slug, EventSlug = item.Slug, Name = item.EventName,
                    Url = events.TryGetValue(item.Slug, out var ev) ? NotificationLinks.ForEvent(ev) : null,
                    Detail = item.ChangeSummary, Actions = true,
                    ExpectedETag = state.GetValueOrDefault(item.Slug)?.ETag, DecisionId = state.GetValueOrDefault(item.Slug)?.LatestDecisionId,
                };
                var groups = new List<NotificationGroup>();
                var visibleChanges = result.Changed.Where(Visible).Select(Item).ToArray();
                var visibleQuarantine = result.Quarantined.Where(Visible).Select(Item).ToArray();
                if (visibleChanges.Length > 0) groups.Add(new("Discovery changes", visibleChanges));
                if (visibleQuarantine.Length > 0) groups.Add(new("Review", visibleQuarantine));
                if (groups.Count > 0) await notifier.NotifyAsync(new Notification { Subject = "Speaker pipeline — discovery", HtmlBody = "", Groups = groups,
                    DeliveryKey = "discovery-" + run.Document.Id }, ct);
            }
            return result;
        }
        catch
        {
            await reporter.CompleteAsync(run, 0, 0, [new("run_failed", "discovery")], true, CancellationToken.None);
            throw;
        }
    }

    public async Task<TrackerMaintenanceResult> TrackerAsync(CancellationToken ct)
    {
        var run = await reporter.StartAsync("tracker", ct);
        try
        {
            var result = await tracker.RunAsync(ct);
            var current = pipeline.Value.OpportunitiesEnabled
                ? (await opportunities.GetOpportunitiesAsync(ct)).ToDictionary(o => o.Slug)
                : new Dictionary<string, OpportunityRecord>();
            var now = time.GetUtcNow();
            var urgentItems = result.UrgentDeadlines.Concat(current.Values
                .Where(o => OpportunityNotificationPolicy.NeedsSnoozeRescue(o, now))
                .Select(o => new UrgentDeadline(o.Slug, o.Event.Name, o.Event.CfpDeadline!.Value,
                    (int)Math.Ceiling((o.Event.CfpDeadline.Value - time.GetUtcNow()).TotalDays))))
                .DistinctBy(u => u.EventSlug).ToArray();
            foreach (var urgent in urgentItems)
            {
                var snoozed = current.TryGetValue(urgent.EventSlug, out var opportunity) && opportunity.IsSnoozed(time.GetUtcNow());
                if (opportunity is not null && !OpportunityNotificationPolicy.IsActionable(opportunity, now)) continue;
                if (snoozed && urgent.Deadline > time.GetUtcNow().AddHours(48)) continue;
                var notice = UrgentDeadlineNotice.Build(urgent.EventSlug, urgent.EventName, urgent.Deadline, urgent.DaysRemaining);
                if (snoozed) notice = notice with { DedupeKey = OpportunityNotificationPolicy.SnoozeRescueKey(urgent.EventSlug, urgent.Deadline) };
                await notifier.NotifyAsync(notice with { Groups = [new("Deadline approaching", [new NotificationItem
                {
                    Id = urgent.EventSlug, EventSlug = urgent.EventSlug, Name = urgent.EventName,
                    ExpectedETag = opportunity?.ETag, DecisionId = opportunity?.LatestDecisionId,
                    Url = opportunity is null ? null : NotificationLinks.ForEvent(opportunity.Event), Detail = $"CFP closes {urgent.Deadline:MMM d}; {urgent.DaysRemaining} days remaining." + (snoozed ? " One reminder before your snooze expires." : ""), Actions = true,
                }])] }, ct);
            }
            var changes = result.Updates.Count + result.ConflictChanges.Count;
            await reporter.CompleteAsync(run, result.Considered, changes, result.Issues ?? [], ct: ct);
            bool Visible(string slug) => !current.TryGetValue(slug, out var opportunity)
                || (!opportunity.DoNotResurface && !opportunity.IsSnoozed(time.GetUtcNow()));
            var updates = result.Updates.Where(u => Visible(u.EventSlug)).ToArray();
            var conflicts = result.ConflictChanges.Where(u => Visible(u.EventSlug)).ToArray();
            if (updates.Length + conflicts.Length > 0)
            {
                var events = current.Count > 0 ? current.ToDictionary(p => p.Key, p => OpportunityMapping.Project(p.Value))
                    : (await api.GetEventsAsync(ct: ct)).ToDictionary(e => e.Slug);
                await TrackerNotification.SendAsync(notifier, updates, conflicts, ct, events, "tracker-" + run.Document.Id, current);
            }
            return result;
        }
        catch
        {
            await reporter.CompleteAsync(run, 0, 0, [new("run_failed", "tracker")], true, CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<ScoredVerdict>> ScoringAsync(CancellationToken ct)
    {
        var run = await reporter.StartAsync("scoring", ct);
        try
        {
            var report = await scoring.RunWithReportAsync(ct);
            await reporter.CompleteAsync(run, report.CandidatesConsidered, report.Decisions.Count,
                report.Errors.Select(e => new RunIssue(e.Code, "scoring")).ToArray(), ct: ct,
                metrics: new Dictionary<string, long>
                {
                    ["input tokens"] = report.InputTokens, ["completion tokens"] = report.CompletionTokens,
                    ["budgeted input tokens"] = report.BudgetedInputTokens, ["budgeted completion tokens"] = report.BudgetedCompletionTokens,
                    ["usage incomplete"] = report.BudgetedInputTokens != report.InputTokens || report.BudgetedCompletionTokens != report.CompletionTokens ? 1 : 0,
                    ["cached candidates"] = report.CachedCandidates, ["deferred candidates"] = report.DeferredCandidates,
                }, modelDeployment: scoringOptions.Value.ModelName);
            var snapshot = pipeline.Value.OpportunitiesEnabled ? await opportunities.GetOpportunitiesAsync(ct)
                : (await api.GetEventsAsync(ct: ct)).Select(e => new OpportunityRecord
                { Event = e, UserStatus = OpportunityMapping.FromLegacy(e.Category), DoNotResurface = e.DoNotResurface,
                    LatestDecision = report.Decisions.FirstOrDefault(d => d.Decision.EventSlug == e.Slug)?.Decision }).ToArray();
            await ScoringNotification.SendSnapshotAsync(notifier, snapshot, report.Decisions, time.GetUtcNow(), ct);
            return report.Decisions;
        }
        catch
        {
            await reporter.CompleteAsync(run, 0, 0, [new("run_failed", "scoring")], true, CancellationToken.None);
            throw;
        }
    }
}
