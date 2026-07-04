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
