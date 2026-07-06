using SpeakerPipeline.Core;

namespace SpeakerPipeline.Telegram.Tests;

/// <summary>
/// In-memory ingest service that records the URLs the router ingests and returns
/// a configurable result, so /track can be exercised without HTTP or an LLM.
/// </summary>
internal sealed class FakeIngestService : IEventIngestService
{
    public List<(string Url, string? Note)> Calls { get; } = [];
    public IngestResult Result { get; set; } = new()
    {
        Status = IngestStatus.Created,
        Message = "Tracked \"Great Lakes Cloud Conf\" — queued for scoring.",
        Slug = "great-lakes-cloud-conf",
        EventName = "Great Lakes Cloud Conf",
        Category = EventCategory.Monitor,
        Confidence = 7,
    };

    public Task<IngestResult> IngestAsync(string url, string? note = null, CancellationToken ct = default)
    {
        Calls.Add((url, note));
        return Task.FromResult(Result);
    }
}
