using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Agents.TrackerMaintenance;

/// <summary>
/// Reconciles each event's <see cref="EventCategory"/> with the aggregate state
/// of its submissions — the deterministic half of the pipeline that the scoring
/// agent's decisions don't cover. Once a talk is submitted, accepted, or turned
/// down, the event should reflect that instead of sitting in the candidate pool.
///
/// Rules-based on purpose: category-from-status is a fixed mapping, so there is
/// no model here. Idempotent by construction (only writes when the derived
/// category differs), so retry-on-failure is safe — see
/// docs/architecture-table-storage.md gotcha 7.
/// </summary>
public sealed class TrackerMaintenanceAgent
{
    public const string ActivitySourceName = "SpeakerPipeline.Agents.TrackerMaintenance";
    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    private readonly ISpeakerPipelineApiClient _api;
    private readonly ILogger<TrackerMaintenanceAgent> _logger;
    private readonly TrackerMaintenanceOptions _options;

    public TrackerMaintenanceAgent(
        ISpeakerPipelineApiClient api,
        IOptions<TrackerMaintenanceOptions> options,
        ILogger<TrackerMaintenanceAgent> logger)
    {
        _api = api;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Walks the tracked events, derives each one's category from its submissions,
    /// and writes back the ones that changed. Returns the changes made (for
    /// observability and tests).
    /// </summary>
    public async Task<TrackerMaintenanceResult> RunAsync(CancellationToken ct = default)
    {
        using var runActivity = ActivitySource.StartActivity("tracker-maintenance.run");
        runActivity?.SetTag("agent.name", _options.AgentName);
        runActivity?.SetTag("agent.version", _options.AgentVersion);

        var events = await _api.GetEventsAsync(ct: ct);
        var blackouts = await _api.GetBlackoutsAsync(ct);
        var considered = 0;
        var updates = new List<TrackerUpdate>();
        var conflicts = new List<ConflictChange>();

        foreach (var ev in events.Take(_options.MaxEventsPerRun))
        {
            considered++;

            // Terminal categories are left alone — including their conflict flags.
            if (ev.Category is EventCategory.Delivered or EventCategory.Skip)
            {
                continue;
            }

            var submissions = await _api.GetSubmissionsForEventAsync(ev.Slug, ct);
            var derived = DeriveCategory(ev.Category, submissions.Select(s => s.Status));
            var newCategory = derived is { } d && d != ev.Category ? d : ev.Category;

            // Deterministic conflict flags from blackouts + committed engagements (C1).
            var (family, prep) = ConflictEvaluator.Evaluate(
                ev, blackouts, events, _options.PrepWindowDays, _options.PrepCongestionThreshold);

            var categoryChanged = newCategory != ev.Category;
            var flagsChanged = family != ev.FamilyConflictFlag || prep != ev.PrepConflictFlag;
            if (!categoryChanged && !flagsChanged)
            {
                continue;
            }

            var updated = ev with
            {
                Category = newCategory,
                FamilyConflictFlag = family,
                PrepConflictFlag = prep,
                // A category change owns StatusDetail; a flag-only change leaves it intact.
                StatusDetail = categoryChanged
                    ? $"Category set to {newCategory} from submission status by {_options.AgentName}-{_options.AgentVersion}."
                    : ev.StatusDetail,
            };

            try
            {
                await _api.UpsertEventAsync(updated, ct);
                if (categoryChanged)
                {
                    updates.Add(new TrackerUpdate(ev.Slug, ev.Category, newCategory));
                    _logger.LogInformation("Tracker: {Slug} {From} -> {To}", ev.Slug, ev.Category, newCategory);
                }
                if (flagsChanged)
                {
                    conflicts.Add(new ConflictChange(ev.Slug, family, prep));
                    _logger.LogInformation("Tracker: {Slug} conflicts family={Family} prep={Prep}", ev.Slug, family, prep);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Tracker: failed to update event {Slug}", ev.Slug);
            }
        }

        // Daily deadline sweep (B3): flag SubmitNow events whose CFP deadline is
        // imminent so the weekly scoring digest isn't the first alert.
        var urgent = FindUrgentDeadlines(events, DateTimeOffset.UtcNow, _options.UrgentDeadlineDays);

        runActivity?.SetTag("events.considered", considered);
        runActivity?.SetTag("events.updated", updates.Count);
        runActivity?.SetTag("events.conflicts", conflicts.Count);
        runActivity?.SetTag("deadlines.urgent", urgent.Count);
        _logger.LogInformation("Tracker-maintenance run complete. {Updated}/{Considered} updated, {Conflicts} conflict changes, {Urgent} urgent deadlines.",
            updates.Count, considered, conflicts.Count, urgent.Count);
        return new TrackerMaintenanceResult(updates, conflicts, urgent);
    }

    /// <summary>
    /// Selects the <see cref="EventCategory.SubmitNow"/> events whose CFP deadline
    /// is still ahead but within the urgency window. Pure and clock-injected so
    /// the sweep is unit-testable without the API.
    /// </summary>
    internal static IReadOnlyList<UrgentDeadline> FindUrgentDeadlines(
        IReadOnlyList<EventRecord> events, DateTimeOffset now, int urgentDays)
    {
        var today = now.UtcDateTime.Date;
        var cutoffDate = today.AddDays(urgentDays);
        return
        [
            .. events
                .Where(e => e.Category == EventCategory.SubmitNow
                            && !e.DoNotResurface
                            && e.CfpDeadline is { } d
                            && d >= now
                            && d.UtcDateTime.Date <= cutoffDate)
                .OrderBy(e => e.CfpDeadline)
                .Select(e =>
                {
                    var d = e.CfpDeadline!.Value;
                    return new UrgentDeadline(e.Slug, e.Name, d, (d.UtcDateTime.Date - today).Days);
                }),
        ];
    }

    /// <summary>
    /// Derives the category an event should carry from its submissions' statuses.
    /// Precedence: an <see cref="SubmissionStatus.Accepted"/> wins; else anything
    /// still in flight (<see cref="SubmissionStatus.Submitted"/> /
    /// <see cref="SubmissionStatus.InReview"/>) means the event has been submitted;
    /// else every submission was <see cref="SubmissionStatus.Rejected"/> or
    /// <see cref="SubmissionStatus.Withdrawn"/>, so pass.
    ///
    /// Returns <c>null</c> — leave the event untouched — when it has no submissions
    /// (the scoring agent or a human owns its category), or when it sits in a
    /// terminal/manual category the tracker must not override:
    /// <see cref="EventCategory.Delivered"/> (the event already happened) or
    /// <see cref="EventCategory.Skip"/> (an explicit human skip).
    /// </summary>
    internal static EventCategory? DeriveCategory(EventCategory current, IEnumerable<SubmissionStatus> statuses)
    {
        if (current is EventCategory.Delivered or EventCategory.Skip)
        {
            return null;
        }

        var list = statuses as IReadOnlyCollection<SubmissionStatus> ?? [.. statuses];
        if (list.Count == 0)
        {
            return null;
        }

        if (list.Any(s => s is SubmissionStatus.Accepted))
        {
            return EventCategory.Accepted;
        }

        if (list.Any(s => s is SubmissionStatus.Submitted or SubmissionStatus.InReview))
        {
            return EventCategory.Submitted;
        }

        // Only Rejected / Withdrawn remain.
        return EventCategory.Pass;
    }
}

/// <summary>A category change the tracker applied to one event.</summary>
public sealed record TrackerUpdate(string EventSlug, EventCategory From, EventCategory To);

/// <summary>A change to an event's conflict flags on this run.</summary>
public sealed record ConflictChange(string EventSlug, bool Family, bool Prep);

/// <summary>A SubmitNow event whose CFP deadline is imminent (within the urgency window).</summary>
public sealed record UrgentDeadline(string EventSlug, string EventName, DateTimeOffset Deadline, int DaysRemaining);

/// <summary>The full outcome of a tracker-maintenance run: category reconciles, conflict changes, imminent deadlines.</summary>
public sealed record TrackerMaintenanceResult(
    IReadOnlyList<TrackerUpdate> Updates,
    IReadOnlyList<ConflictChange> ConflictChanges,
    IReadOnlyList<UrgentDeadline> UrgentDeadlines);
