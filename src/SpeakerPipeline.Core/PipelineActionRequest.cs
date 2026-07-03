namespace SpeakerPipeline.Core;

/// <summary>
/// A request to move a tracked event through the pipeline. The closed
/// <see cref="PipelineAction"/> set enforces the ambiguity guard: the caller
/// states intent vs. confirmation explicitly, so the API never has to guess.
/// </summary>
public sealed record PipelineActionRequest
{
    public required PipelineAction Action { get; init; }

    /// <summary>Optional free-text note recorded on the event's status detail.</summary>
    public string? Note { get; init; }
}
