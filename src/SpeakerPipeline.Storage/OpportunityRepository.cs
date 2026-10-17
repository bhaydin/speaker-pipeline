using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using Azure.Data.Tables;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Storage;

internal sealed class OpportunityRepository(TableClient client, TimeProvider clock, bool requireMigration = false) : IOpportunityRepository
{
    private bool _migrationVerified;
    internal const string CurrentRow = "current";
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public async Task<OpportunityRecord?> GetAsync(string slug, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct).ConfigureAwait(false);
        var response = await client.GetEntityIfExistsAsync<TableEntity>(slug, CurrentRow, cancellationToken: ct).ConfigureAwait(false);
        return response.HasValue ? Read(response.Value!) : null;
    }

    public async Task<IReadOnlyList<OpportunityRecord>> GetAllAsync(CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct).ConfigureAwait(false);
        var results = new List<OpportunityRecord>();
        await foreach (var row in client.QueryAsync<TableEntity>("RowKey eq 'current'", cancellationToken: ct).ConfigureAwait(false))
            results.Add(Read(row));
        return results.OrderBy(r => r.Event.CfpDeadline ?? DateTimeOffset.MaxValue).ThenBy(r => r.Event.Name, StringComparer.Ordinal).ToArray();
    }

    public async Task<IReadOnlyList<OpportunityHistoryEntry>> GetHistoryAsync(string slug, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct).ConfigureAwait(false);
        var result = new List<OpportunityHistoryEntry>();
        await foreach (var row in client.QueryAsync<TableEntity>(TableClient.CreateQueryFilter($"PartitionKey eq {slug} and Kind eq 'History'"), cancellationToken: ct).ConfigureAwait(false))
            result.Add(Deserialize<OpportunityHistoryEntry>(ReadPayload(row, "Json")));
        return result.OrderByDescending(r => r.AtUtc).ToArray();
    }

    public async Task<IReadOnlyList<OpportunityHistoryEntry>> GetFeedbackAsync(int limit = 20, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct).ConfigureAwait(false);
        var result = new List<OpportunityHistoryEntry>();
        await foreach (var row in client.QueryAsync<TableEntity>("Kind eq 'History' and IsFeedback eq true", cancellationToken: ct).ConfigureAwait(false))
            result.Add(Deserialize<OpportunityHistoryEntry>(ReadPayload(row, "Json")));
        var retired = result.Where(e => e.SupersedesFeedbackId is not null).Select(e => (e.EventSlug, e.SupersedesFeedbackId)).ToHashSet();
        return result.Where(e => e.Kind == "Feedback" && !retired.Contains((e.EventSlug, e.Id)))
            .OrderByDescending(r => r.AtUtc).Take(Math.Clamp(limit, 1, 100)).ToArray();
    }

    public async Task<OpportunityRecord> ApplyActionAsync(string slug, OpportunityActionRequest request, string expectedETag, string actor, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct).ConfigureAwait(false);
        var fingerprint = Hash(JsonSerializer.Serialize(new { request, actor }, Json));
        var replay = await ReplayAsync(slug, request.IdempotencyKey, fingerprint, ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        var entity = await LoadAsync(slug, ct).ConfigureAwait(false);
        var current = Read(entity);
        RequireETag(current, expectedETag);
        var now = clock.GetUtcNow();
        var supersedes = request.Action == OpportunityAction.RetireFeedback ? request.FeedbackId : request.SupersedesFeedbackId;
        OpportunityHistoryEntry? originalFeedback = null;
        var priorHistory = supersedes is not null || current.LatestDecisionId is not null ? await GetHistoryAsync(slug, ct).ConfigureAwait(false) : [];
        if (supersedes is not null)
        {
            originalFeedback = priorHistory.FirstOrDefault(e => e.Id == supersedes && e.Kind == "Feedback");
            if (originalFeedback is null || priorHistory.Any(e => e.SupersedesFeedbackId == supersedes))
                throw new ArgumentException("Feedback to retire or correct must be an active entry on this opportunity.");
            if (request.Action is not (OpportunityAction.Feedback or OpportunityAction.RetireFeedback))
                throw new ArgumentException("Only feedback corrections may supersede an entry.");
            if (request.DecisionId is not null && request.DecisionId != originalFeedback.DecisionId)
                throw new ArgumentException("A feedback correction must target its original decision.");
        }
        var updated = OpportunityTransitions.Apply(current, originalFeedback is null ? request : request with { DecisionId = null }, now);
        var scoredContext = originalFeedback ?? priorHistory.FirstOrDefault(e => e.Id == current.LatestDecisionId);
        var history = new OpportunityHistoryEntry
        {
            Id = HistoryId(now, request.IdempotencyKey), Kind = request.Action == OpportunityAction.RetireFeedback ? "FeedbackRetired" : request.Action == OpportunityAction.Feedback || request.ReasonCode is not null ? "Feedback" : "Action",
            AtUtc = now, Actor = actor, Status = updated.Status, Reason = request.Note ?? request.Action.ToString(),
            EventSlug = originalFeedback?.EventSlug ?? current.Slug, EventName = scoredContext?.EventName ?? current.Event.Name,
            Decision = originalFeedback is null ? current.LatestDecision : originalFeedback.Decision,
            EventSnapshot = scoredContext?.EventSnapshot ?? (current.LatestDecisionId is null && originalFeedback is null ? current.Event with { ETag = null } : null),
            ReasonCode = request.ReasonCode, Agree = request.Agree, DecisionId = originalFeedback is null ? request.DecisionId ?? current.LatestDecisionId : originalFeedback.DecisionId,
            SupersedesFeedbackId = supersedes,
        };
        return await CommitAsync(entity, updated, history, request.IdempotencyKey, fingerprint, ct).ConfigureAwait(false);
    }

    public Task<OpportunityRecord> ApplyDecisionAsync(OpportunityScoringRequest request, CancellationToken ct = default)
        => ApplyDecisionAsync(request, null, ct);

    public async Task<OpportunityRecord> ApplyDecisionAsync(OpportunityScoringRequest request, CatalogSnapshot? catalog, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct).ConfigureAwait(false);
        var slug = request.Decision.EventSlug;
        var fingerprint = Hash(JsonSerializer.Serialize(request, Json));
        var replay = await ReplayAsync(slug, request.IdempotencyKey, fingerprint, ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var entity = await LoadAsync(slug, ct).ConfigureAwait(false);
            var current = Read(entity);
            if (string.IsNullOrWhiteSpace(request.ExpectedETag) || request.ExpectedETag == "*")
                throw new ArgumentException("Scoring requires an observed exact ETag.");
            if (current.ScoringInputRevision != request.ScoringInputRevision)
                throw new OpportunityConflictException("stale_scoring_input", current, staleScoringInput: true);
            var decision = request.Decision;
            var now = clock.GetUtcNow();
            OpportunityDecisionPolicy.Validate(current, request, now, catalog);
            var id = HistoryId(now, request.IdempotencyKey);
            var updated = current with
            {
                SystemVerdict = decision.Recommendation, LatestDecision = decision, LatestDecisionId = id,
                Event = current.Event with { NextAction = decision.Rationale, DecidedByAgent = decision.DecidedByAgent },
            };
            var history = new OpportunityHistoryEntry
            {
                Id = id, Kind = "Verdict", AtUtc = now, Actor = decision.DecidedByAgent ?? "scoring-agent",
                EventSlug = current.Slug, EventName = current.Event.Name, EventSnapshot = current.Event with { ETag = null },
                PreviousVerdict = current.SystemVerdict, Verdict = decision.Recommendation, Reason = decision.Rationale, Decision = decision,
            };
            try { return await CommitAsync(entity, updated, history, request.IdempotencyKey, fingerprint, ct).ConfigureAwait(false); }
            catch (OpportunityConflictException) when (attempt < 2) { }
        }
        throw new OpportunityConflictException("Opportunity changed repeatedly while committing a score.");
    }

    public async Task<OpportunityRecord> UpsertFactsAsync(EventRecord record, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct).ConfigureAwait(false);
        if (!SlugSanitizer.IsValid(record.Slug)) throw new ArgumentException("Invalid opportunity slug.");
        var response = await client.GetEntityIfExistsAsync<TableEntity>(record.Slug, CurrentRow, cancellationToken: ct).ConfigureAwait(false);
        if (!response.HasValue)
        {
            if (!string.IsNullOrEmpty(record.ETag)) throw new OpportunityConflictException("The opportunity no longer exists.");
            var created = new OpportunityRecord
            {
                Event = record with { ETag = null, ScoringInputRevision = 0, RepeatTalkOverride = false, RecordedSubmissionStatus = null },
                ETag = NewVersion(),
                UserStatus = record.Category == EventCategory.Quarantine ? OpportunityStatus.Review : null,
                PlacementSource = PlacementSource.Pipeline,
            };
            var row = Write(new TableEntity(record.Slug, CurrentRow), created);
            try
            {
                await client.AddEntityAsync(row, ct).ConfigureAwait(false);
                return (await GetAsync(record.Slug, ct).ConfigureAwait(false))!;
            }
            catch (RequestFailedException ex) when (ex.Status == 409) { throw new OpportunityConflictException("Opportunity was concurrently created."); }
        }
        var original = response.Value!;
        var current = Read(original);
        RequireETag(current, record.ETag);
        var facts = record with
        {
            Category = current.Event.Category, Priority = current.Event.Priority, DoNotResurface = current.DoNotResurface,
            NextAction = current.Event.NextAction, DecidedByAgent = current.Event.DecidedByAgent,
            ETag = null, ScoringInputRevision = 0, RepeatTalkOverride = current.RepeatTalkOverride, RecordedSubmissionStatus = null,
        };
        // Verification timestamps alone do not invalidate an in-flight model decision.
        var previous = current.Event with { ETag = null, ScoringInputRevision = 0, LastVerifiedUtc = null, RepeatTalkOverride = current.RepeatTalkOverride, RecordedSubmissionStatus = null };
        var material = JsonSerializer.Serialize(previous, Json) != JsonSerializer.Serialize(facts with { LastVerifiedUtc = null }, Json);
        var updated = current with { Event = facts, ScoringInputRevision = current.ScoringInputRevision + (material ? 1 : 0) };
        var now = clock.GetUtcNow();
        var key = Guid.NewGuid().ToString("n");
        return await CommitAsync(original, updated, new OpportunityHistoryEntry
        {
            Id = HistoryId(now, key), Kind = "Facts", AtUtc = now, Actor = "pipeline", Reason = "Opportunity facts updated",
            EventSlug = current.Slug, EventName = current.Event.Name,
        }, key, Hash(JsonSerializer.Serialize(facts, Json)), ct).ConfigureAwait(false);
    }

    private async Task<OpportunityRecord> CommitAsync(TableEntity original, OpportunityRecord updated, OpportunityHistoryEntry history, string key, string fingerprint, CancellationToken ct)
    {
        ValidateKey(key);
        // The API version is allocated before committing so the current row and immutable
        // command receipt contain exactly the same accepted result. Azure's physical ETag
        // remains the transaction precondition, separate from this opaque API token.
        updated = updated with { ETag = NewVersion() };
        var row = Write(original, updated);
        var audit = new TableEntity(updated.Slug, history.Id)
        {
            ["Kind"] = "History", ["IsFeedback"] = history.Kind is "Feedback" or "FeedbackRetired",
        };
        WritePayload(audit, "Json", JsonSerializer.Serialize(history, Json));
        // A command result is immutable. The token returned by replay identifies the accepted version,
        // not necessarily today's version; consumers may refresh before rendering subsequent changes.
        var command = new TableEntity(updated.Slug, "command-" + Hash(key))
        {
            ["Kind"] = "Command", ["Fingerprint"] = fingerprint,
        };
        WritePayload(command, "Result", JsonSerializer.Serialize(updated, Json));
        try
        {
            await client.SubmitTransactionAsync([
                new TableTransactionAction(TableTransactionActionType.UpdateReplace, row, original.ETag),
                new TableTransactionAction(TableTransactionActionType.Add, audit),
                new TableTransactionAction(TableTransactionActionType.Add, command),
            ], ct).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            var replay = await ReplayAsync(updated.Slug, key, fingerprint, ct).ConfigureAwait(false);
            if (replay is not null) return replay;
            throw new OpportunityConflictException("Opportunity changed. Refresh before retrying.", await GetAsync(updated.Slug, ct).ConfigureAwait(false));
        }
        return updated;
    }

    public async Task<OpportunityRecord> ReconcileSubmissionsAsync(string slug, IReadOnlyList<SubmissionRecord> submissions, string expectedETag, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct).ConfigureAwait(false);
        if (submissions.Any(s => s.EventSlug != slug || !Enum.IsDefined(s.Status))) throw new ArgumentException("Submission evidence must belong to this opportunity.");
        var original = await LoadAsync(slug, ct).ConfigureAwait(false);
        var current = Read(original);
        RequireETag(current, expectedETag);
        SubmissionStatus? status = submissions.Count == 0 ? null
            : submissions.Any(s => s.Status == SubmissionStatus.Accepted) ? SubmissionStatus.Accepted
            : submissions.Any(s => s.Status is SubmissionStatus.Submitted or SubmissionStatus.InReview) ? SubmissionStatus.Submitted
            : submissions.Any(s => s.Status == SubmissionStatus.Rejected) ? SubmissionStatus.Rejected : SubmissionStatus.Withdrawn;
        if (current.RecordedSubmissionStatus == status) return current;
        var now = clock.GetUtcNow();
        var key = "submission-" + Hash(expectedETag + ":" + status);
        var updated = current with { RecordedSubmissionStatus = status, ScoringInputRevision = current.ScoringInputRevision + 1 };
        return await CommitAsync(original, updated, new OpportunityHistoryEntry
        {
            Id = HistoryId(now, key), Kind = "SubmissionEvidence", AtUtc = now, Actor = "tracker",
            EventSlug = slug, EventName = current.Event.Name, Status = updated.Status,
            Reason = $"Recorded submissions: {status?.ToString() ?? "none"}; user placement preserved.",
        }, key, Hash(JsonSerializer.Serialize(submissions, Json)), ct).ConfigureAwait(false);
    }

    private async Task<OpportunityRecord?> ReplayAsync(string slug, string key, string fingerprint, CancellationToken ct)
    {
        ValidateKey(key);
        var response = await client.GetEntityIfExistsAsync<TableEntity>(slug, "command-" + Hash(key), cancellationToken: ct).ConfigureAwait(false);
        if (!response.HasValue) return null;
        if (response.Value!.GetString("Fingerprint") != fingerprint)
            throw new ArgumentException("Idempotency key has already been used for a different command.");
        return Deserialize<OpportunityRecord>(ReadPayload(response.Value, "Result"));
    }

    private async Task<TableEntity> LoadAsync(string slug, CancellationToken ct)
    {
        var response = await client.GetEntityIfExistsAsync<TableEntity>(slug, CurrentRow, cancellationToken: ct).ConfigureAwait(false);
        return response.HasValue ? response.Value! : throw new KeyNotFoundException("Opportunity was not found.");
    }

    private async Task EnsureReadyAsync(CancellationToken ct)
    {
        if (!requireMigration || _migrationVerified) return;
        var marker = await client.GetEntityIfExistsAsync<TableEntity>("__migration", "ready", cancellationToken: ct).ConfigureAwait(false);
        if (!marker.HasValue || string.IsNullOrEmpty(marker.Value!.GetString("SnapshotChecksum")))
            throw new InvalidOperationException("Opportunity authority requires a completed, verified frozen migration.");
        _migrationVerified = true;
    }

    internal static OpportunityRecord Read(TableEntity entity)
    {
        var state = Deserialize<OpportunityRecord>(ReadPayload(entity, "OpportunityJson"));
        // Existing/migrated rows predate logical API versions and remain usable without a rewrite.
        return string.IsNullOrEmpty(state.ETag) ? state with { ETag = entity.ETag.ToString() } : state;
    }

    internal static TableEntity Write(TableEntity original, OpportunityRecord record)
    {
        var row = new TableEntity(original.ToDictionary(p => p.Key, p => p.Value)) { PartitionKey = record.Slug, RowKey = CurrentRow, ETag = original.ETag };
        // Preserve unknown source columns. JSON is authoritative; typed fields support future filters.
        row["Kind"] = "Current";
        WritePayload(row, "OpportunityJson", JsonSerializer.Serialize(record, Json));
        row["Status"] = record.Status.ToString();
        row["CfpDeadline"] = record.Event.CfpDeadline?.UtcDateTime;
        row["ScoringInputRevision"] = record.ScoringInputRevision;
        return row;
    }

    // Table strings are limited to 64 KiB. Chunk the encoded JSON rather than truncating
    // source notes, feedback snapshots, or command results; old single-property rows still read.
    internal static string ReadPayload(TableEntity entity, string prefix)
    {
        if (entity.GetString(prefix) is { } single) return single;
        var count = entity.GetInt32(prefix + "ChunkCount") ?? throw new InvalidOperationException("Missing opportunity payload.");
        return string.Concat(Enumerable.Range(0, count).Select(i => entity.GetString(prefix + "Chunk" + i) ?? throw new InvalidOperationException("Incomplete opportunity payload.")));
    }

    internal static void WritePayload(TableEntity entity, string prefix, string value)
    {
        const int chunkSize = 14000;
        foreach (var key in entity.Keys.Where(k => k == prefix || k.StartsWith(prefix + "Chunk", StringComparison.Ordinal)).ToArray()) entity.Remove(key);
        if (value.Length <= chunkSize) { entity[prefix] = value; return; }
        var count = (value.Length + chunkSize - 1) / chunkSize;
        if (count > 40) throw new ArgumentException("Opportunity payload exceeds the supported entity size; source data has not been truncated.");
        entity[prefix + "ChunkCount"] = count;
        for (var i = 0; i < count; i++) entity[prefix + "Chunk" + i] = value.Substring(i * chunkSize, Math.Min(chunkSize, value.Length - i * chunkSize));
    }

    private static void RequireETag(OpportunityRecord current, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected) || expected == "*" || current.ETag != expected)
            throw new OpportunityConflictException("An exact current ETag is required.", current);
    }
    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200) throw new ArgumentException("An idempotency key of at most 200 characters is required.");
    }
    private static string HistoryId(DateTimeOffset now, string key) => $"history-{now.UtcTicks:D19}-{Hash(key)}";
    private static string NewVersion() => $"\"{Guid.NewGuid():N}\"";
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Json) ?? throw new InvalidOperationException("Invalid opportunity payload.");
}
