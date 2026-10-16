using System.Text.Json;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Migrate;

internal sealed record ImportBatch(IReadOnlyList<TalkRecord> Talks, IReadOnlyList<EventRecord> Events, IReadOnlyList<SubmissionRecord> Submissions)
{
    public static ImportBatch Load(string directory, bool samples)
    {
        if (!samples) PrivateFiles.ValidatePath(directory);
        List<T> Read<T>(string file)
            => JsonSerializer.Deserialize<List<T>>(File.ReadAllText(Path.Combine(directory, file)), PrivateFiles.Json)
                ?? throw new ArgumentException("All three import files must contain JSON arrays.");
        var talks = Read<TalkSeed>("sample-talks.json");
        var events = Read<EventSeed>("sample-events.json");
        var submissions = Read<SubmissionSeed>("sample-submissions.json");
        if (talks.Any(t => t.PartitionKey != "talks") || events.Any(e => e.PartitionKey != "events"))
            throw new ArgumentException("Unexpected Events or Talks partition; mapping would discard the supplied partition.");
        var batch = new ImportBatch(talks.Select(SeedMapping.ToRecord).ToArray(), events.Select(SeedMapping.ToRecord).ToArray(), submissions.Select(SeedMapping.ToRecord).ToArray());
        batch.Validate(samples);
        return batch;
    }

    public void Validate(bool samples)
    {
        static void Require(bool valid, string message) { if (!valid) throw new ArgumentException(message); }
        static bool Key(string value) => !string.IsNullOrWhiteSpace(value) && SlugSanitizer.IsValid(value);
        Require(Talks.Select(t => t.Slug).Distinct(StringComparer.OrdinalIgnoreCase).Count() == Talks.Count, "Duplicate talk keys.");
        Require(Events.Select(e => e.Slug).Distinct(StringComparer.OrdinalIgnoreCase).Count() == Events.Count, "Duplicate event keys.");
        Require(Submissions.Select(s => (s.EventSlug, s.SubmissionId)).Distinct().Count() == Submissions.Count, "Duplicate submission keys.");
        foreach (var talk in Talks)
        {
            Require(Key(talk.Slug) && !string.IsNullOrWhiteSpace(talk.CanonicalTitle), "Talk key and title are required.");
            Require(talk.SchemaVersion > 0 && talk.DeliveryCount >= 0 && (talk.LengthMinutes is null or > 0), "Invalid talk schema version, delivery count, or duration.");
            if (!samples) Require(!Placeholder(talk.DeckUrl), "Production imports cannot contain placeholder deck URLs.");
        }
        foreach (var item in Events)
        {
            Require(Key(item.Slug) && !string.IsNullOrWhiteSpace(item.Name) && item.SchemaVersion > 0, "Invalid event key, name, or schema version.");
            Require(!item.EventDateStart.HasValue || !item.EventDateEnd.HasValue || item.EventDateEnd >= item.EventDateStart, "Event end precedes its start.");
            if (!samples) Require(!Placeholder(item.CfpUrl) && !Placeholder(item.EventUrl) && !SampleCatalog.EventKeys.Contains(item.Slug), "Production imports cannot contain sample events or placeholder event URLs.");
        }
        foreach (var submission in Submissions)
        {
            Require(Key(submission.EventSlug) && Key(submission.SubmissionId) && Key(submission.TalkSlug), "Invalid submission keys.");
            Require(!string.IsNullOrWhiteSpace(submission.EventName) && !string.IsNullOrWhiteSpace(submission.TalkTitleUsed) && !string.IsNullOrWhiteSpace(submission.AbstractUsed), "Submission event, title, and abstract are required.");
            Require(submission.SubmittedOnUtc > DateTimeOffset.MinValue && submission.SchemaVersion > 0, "Submission timestamp and positive schema version are required.");
            if (!samples) Require(!Placeholder(submission.ContactEmail) && !Placeholder(submission.ContactPerson), "Production imports cannot contain placeholder contacts.");
        }
    }

    public void ValidateReferences(IEnumerable<EventRecord> existingEvents, IEnumerable<TalkRecord> existingTalks)
    {
        var events = existingEvents.Concat(Events).Select(e => e.Slug).ToHashSet(StringComparer.Ordinal);
        var talks = existingTalks.Concat(Talks).Select(t => t.Slug).ToHashSet(StringComparer.Ordinal);
        if (Submissions.Any(s => !events.Contains(s.EventSlug) || !talks.Contains(s.TalkSlug)))
            throw new ArgumentException("A submission references a missing event or talk. Nothing was imported.");
    }

    private static bool Placeholder(string? value)
        => value is not null && (value.Contains("placeholder", StringComparison.OrdinalIgnoreCase) ||
            Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Host is "example.com" or "example.net" or "example.org" || uri.Host.EndsWith(".example.org", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".example.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".example.net", StringComparison.OrdinalIgnoreCase)));
}
