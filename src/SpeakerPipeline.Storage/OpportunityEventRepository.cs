using System.Runtime.CompilerServices;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Storage;

/// <summary>Compatibility reads use the aggregate; legacy Events is never written after cutover.</summary>
internal sealed class OpportunityEventRepository(IOpportunityRepository opportunities, TimeProvider clock) : IEventRepository
{
    public async Task<EventRecord?> GetAsync(string slug, CancellationToken ct = default)
    {
        var value = await opportunities.GetAsync(slug, ct).ConfigureAwait(false);
        return value is null ? null : OpportunityMapping.Project(value);
    }
    public async IAsyncEnumerable<EventRecord> QueryAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var value in await opportunities.GetAllAsync(ct).ConfigureAwait(false)) yield return OpportunityMapping.Project(value);
    }
    public async Task<IReadOnlyList<EventRecord>> GetByCategoryAsync(IReadOnlyList<EventCategory> categories, CancellationToken ct = default)
        => (await opportunities.GetAllAsync(ct).ConfigureAwait(false)).Select(OpportunityMapping.Project).Where(e => categories.Contains(e.Category)).ToArray();
    public async Task<IReadOnlyList<EventRecord>> GetUpcomingDeadlinesAsync(TimeSpan window, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        return (await opportunities.GetAllAsync(ct).ConfigureAwait(false)).Select(OpportunityMapping.Project)
            .Where(e => e.CfpDeadline > now && e.CfpDeadline < now + window).ToArray();
    }
    public async Task UpsertAsync(EventRecord record, CancellationToken ct = default)
        => _ = await opportunities.UpsertFactsAsync(record, ct).ConfigureAwait(false);
    public Task DeleteAsync(string slug, CancellationToken ct = default)
        => throw new InvalidOperationException("Opportunity deletion is disabled; use the reversible Drop action.");
}
