namespace SpeakerPipeline.Core;

/// <summary>
/// The manual ingestion lane: turn a single human-supplied URL into a tracked,
/// scoring-bound event. A human vouched for the URL, so extraction runs at a
/// lower confidence floor than scheduled discovery. Implemented once in the
/// agent tier (fetch → LLM extract → reconcile); consumers (MCP, Telegram) reach
/// it in-process or over HTTP through this abstraction so the logic lives once.
/// </summary>
public interface IEventIngestService
{
    Task<IngestResult> IngestAsync(string url, string? note = null, CancellationToken ct = default);
}

/// <summary>Outcome of a manual ingest attempt.</summary>
public sealed record IngestResult
{
    public required IngestStatus Status { get; init; }

    /// <summary>Human-readable one-liner suitable for a Telegram reply or an AI-session confirmation.</summary>
    public required string Message { get; init; }

    public string? Slug { get; init; }
    public string? EventName { get; init; }
    public EventCategory? Category { get; init; }
    public int Confidence { get; init; }

    public static IngestResult Failed(IngestStatus status, string message) => new() { Status = status, Message = message };
}

/// <summary>What the ingest lane did with a URL.</summary>
public enum IngestStatus
{
    /// <summary>A new event was created and queued for scoring.</summary>
    Created,

    /// <summary>An existing tracked event was updated from the page.</summary>
    Updated,

    /// <summary>The page matched an event already tracked with no new information.</summary>
    AlreadyTracked,

    /// <summary>The page could not be fetched.</summary>
    FetchFailed,

    /// <summary>The page is not a call for speakers / event listing.</summary>
    NotAnEvent,

    /// <summary>Extraction confidence was below even the human-vouched floor.</summary>
    LowConfidence,
}
