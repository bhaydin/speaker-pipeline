using SpeakerPipeline.Core;

namespace SpeakerPipeline.Agents.Discovery;

/// <summary>
/// The structured event the extraction model returns for a source page. Maps to
/// the subset of the tracker's <see cref="EventRecord"/> that a CFP page can
/// supply; scoring (fit/effort/confidence/recommendation) stays the Evaluator's
/// job, so this carries only observed facts, not decisions.
/// </summary>
public sealed record ExtractedEvent
{
    /// <summary>False when the page is not a call for speakers / event listing.</summary>
    public bool IsEvent { get; init; }

    public string EventName { get; init; } = string.Empty;
    public EventType EventType { get; init; } = EventType.Conference;
    public string? Location { get; init; }
    public EventFormat? Format { get; init; }
    public DateTimeOffset? EventStartDate { get; init; }
    public DateTimeOffset? EventEndDate { get; init; }
    public CfpStatus CfpStatus { get; init; } = CfpStatus.Unknown;
    public DateTimeOffset? CfpDeadline { get; init; }
    public string? CfpUrl { get; init; }
    public string? EventUrl { get; init; }
    public IReadOnlyList<Lane> FocusFit { get; init; } = [];
    public TravelBurden? TravelBurden { get; init; }

    /// <summary>0–10 — the model's confidence that the extracted facts are correct.</summary>
    public int Confidence { get; init; }
}

/// <summary>What the discovery agent did with one source page.</summary>
public sealed record DiscoveryResult(string Slug, string EventName, bool IsNew, string ChangeSummary);

/// <summary>
/// The full outcome of a discovery run: what changed in the tracker, what was
/// quarantined for review, and a funnel that accounts for every candidate so
/// silent drops become visible (BUILD_PLAN.GO_FORWARD A3).
/// </summary>
public sealed record DiscoveryRunReport(
    IReadOnlyList<DiscoveryResult> Changed,
    IReadOnlyList<DiscoveryResult> Quarantined,
    DiscoveryFunnel Funnel);

/// <summary>
/// Per-run candidate accounting, from source output to tracker write, with a
/// reason code for every drop. Every fetched candidate lands in exactly one of:
/// changed (new/updated), quarantined, or <see cref="Dropped"/> with a reason —
/// so "the system looks dead" is always explainable, never invisible.
/// </summary>
public sealed record DiscoveryFunnel
{
    /// <summary>Candidates produced by all sources this run.</summary>
    public int Targets { get; init; }

    /// <summary>Candidates that yielded a usable event (IsEvent with a name).</summary>
    public int Extracted { get; init; }

    /// <summary>Extracted candidates whose confidence met the floor.</summary>
    public int PassedFloor { get; init; }

    public int New { get; init; }
    public int Updated { get; init; }
    public int Quarantined { get; init; }

    /// <summary>Drop reason code → count (e.g. <c>not_event</c>, <c>low_confidence</c>, <c>do_not_resurface</c>, <c>cfp_closed</c>, <c>unchanged</c>).</summary>
    public IReadOnlyDictionary<string, int> Dropped { get; init; } = new Dictionary<string, int>();

    /// <summary>Source name → candidate count, so fuel provenance is visible.</summary>
    public IReadOnlyDictionary<string, int> CandidatesBySource { get; init; } = new Dictionary<string, int>();

    // --- Cost levers -------------------------------------------------------
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long TotalTokens => InputTokens + OutputTokens;

    /// <summary>Google PSE queries billed this run (0 when search is disabled).</summary>
    public int SearchQueries { get; init; }
}
