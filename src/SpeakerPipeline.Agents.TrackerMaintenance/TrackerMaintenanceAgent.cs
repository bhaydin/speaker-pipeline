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
    public async Task<IReadOnlyList<TrackerUpdate>> RunAsync(CancellationToken ct = default)
    {
        using var runActivity = ActivitySource.StartActivity("tracker-maintenance.run");
        runActivity?.SetTag("agent.name", _options.AgentName);
        runActivity?.SetTag("agent.version", _options.AgentVersion);

        var events = await _api.GetEventsAsync(ct: ct);
        var considered = 0;
        var updates = new List<TrackerUpdate>();

        foreach (var ev in events.Take(_options.MaxEventsPerRun))
        {
            considered++;

            if (ev.Category is EventCategory.Delivered or EventCategory.Skip)
            {
                continue;
            }

            var submissions = await _api.GetSubmissionsForEventAsync(ev.Slug, ct);
            var derived = DeriveCategory(ev.Category, submissions.Select(s => s.Status));
            if (derived is null || derived == ev.Category)
            {
                continue;
            }

            var updated = ev with
            {
                StatusDetail = $"Category set to {derived.Value} from submission status by {_options.AgentName}-{_options.AgentVersion}.",

            try
            {
                await _api.UpsertEventAsync(updated, ct);
                updates.Add(new TrackerUpdate(ev.Slug, ev.Category, derived.Value));
                _logger.LogInformation("Tracker: {Slug} {From} -> {To}", ev.Slug, ev.Category, derived.Value);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Tracker: failed to update event {Slug}", ev.Slug);
            }
        }

        runActivity?.SetTag("events.considered", considered);
        runActivity?.SetTag("events.updated", updates.Count);
        _logger.LogInformation("Tracker-maintenance run complete. {Updated}/{Considered} updated.", updates.Count, considered);
        return updates;
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
