using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Api.Tests;

/// <summary>
/// Spins up the API with in-memory repository fakes. Avoids hitting Azure
/// from unit tests while still exercising the real ASP.NET Core pipeline,
/// validation, auth (dev), and OpenAPI surface.
/// </summary>
public sealed class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    public InMemoryEventRepository Events { get; } = new();
    public InMemorySubmissionRepository Submissions { get; } = new();
    public InMemoryTalkRepository Talks { get; } = new();
    public InMemoryTopicRepository Topics { get; } = new();
    public InMemoryBlackoutRepository Blackouts { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:TableEndpoint"] = "https://test.example.org",
                ["Authentication:Authority"] = "https://login.microsoftonline.com/test/v2.0",
                ["Authentication:Audience"] = "test-audience",
                ["ApplicationInsights:ConnectionString"] = "",
            });
        });

        builder.ConfigureServices(services =>
        {
            // Replace real repositories with in-memory fakes.
            services.RemoveAll<IEventRepository>();
            services.RemoveAll<ISubmissionRepository>();
            services.RemoveAll<ITalkRepository>();
            services.RemoveAll<ITopicRepository>();
            services.RemoveAll<IBlackoutRepository>();

            services.AddSingleton<IEventRepository>(Events);
            services.AddSingleton<ISubmissionRepository>(Submissions);
            services.AddSingleton<ITalkRepository>(Talks);
            services.AddSingleton<ITopicRepository>(Topics);
            services.AddSingleton<IBlackoutRepository>(Blackouts);
        });
    }
}

public sealed class InMemoryEventRepository : IEventRepository
{
    private readonly ConcurrentDictionary<string, EventRecord> _items = new(StringComparer.OrdinalIgnoreCase);

    public Task<EventRecord?> GetAsync(string slug, CancellationToken ct = default)
        => Task.FromResult(_items.TryGetValue(slug, out var r) ? r : null);

    public async IAsyncEnumerable<EventRecord> QueryAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var item in _items.Values)
        {
            yield return item;
        }
        await Task.CompletedTask;
    }

    public Task<IReadOnlyList<EventRecord>> GetByCategoryAsync(IReadOnlyList<EventCategory> categories, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<EventRecord>>([.. _items.Values.Where(e => categories.Contains(e.Category))]);

    public Task<IReadOnlyList<EventRecord>> GetUpcomingDeadlinesAsync(TimeSpan window, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.Add(window);
        return Task.FromResult<IReadOnlyList<EventRecord>>(
            [.. _items.Values.Where(e => e.CfpDeadline.HasValue && e.CfpDeadline > DateTimeOffset.UtcNow && e.CfpDeadline < cutoff)]);
    }

    public Task UpsertAsync(EventRecord record, CancellationToken ct = default)
    {
        _items[record.Slug] = record;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string slug, CancellationToken ct = default)
    {
        _items.TryRemove(slug, out _);
        return Task.CompletedTask;
    }
}

public sealed class InMemorySubmissionRepository : ISubmissionRepository
{
    private readonly ConcurrentDictionary<(string, string), SubmissionRecord> _items = new();

    public Task<SubmissionRecord?> GetAsync(string eventSlug, string submissionId, CancellationToken ct = default)
        => Task.FromResult(_items.TryGetValue((eventSlug, submissionId), out var r) ? r : null);

    public Task<IReadOnlyList<SubmissionRecord>> GetForEventAsync(string eventSlug, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SubmissionRecord>>(
            [.. _items.Values.Where(s => string.Equals(s.EventSlug, eventSlug, StringComparison.OrdinalIgnoreCase))]);

    public Task UpsertAsync(SubmissionRecord record, CancellationToken ct = default)
    {
        _items[(record.EventSlug, record.SubmissionId)] = record;
        return Task.CompletedTask;
    }
}

public sealed class InMemoryTalkRepository : ITalkRepository
{
    private readonly ConcurrentDictionary<string, TalkRecord> _items = new(StringComparer.OrdinalIgnoreCase);

    public Task<TalkRecord?> GetAsync(string slug, CancellationToken ct = default)
        => Task.FromResult(_items.TryGetValue(slug, out var r) ? r : null);

    public Task<IReadOnlyList<TalkRecord>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TalkRecord>>([.. _items.Values]);

    public Task<IReadOnlyList<TalkRecord>> GetByLaneAsync(Lane lane, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TalkRecord>>([.. _items.Values.Where(t => t.Lane == lane)]);

    public Task UpsertAsync(TalkRecord record, CancellationToken ct = default)
    {
        _items[record.Slug] = record;
        return Task.CompletedTask;
    }
}

public sealed class InMemoryTopicRepository : ITopicRepository
{
    private readonly ConcurrentDictionary<string, TopicRecord> _items = new(StringComparer.OrdinalIgnoreCase);

    public Task<TopicRecord?> GetAsync(string topicId, CancellationToken ct = default)
        => Task.FromResult(_items.TryGetValue(topicId, out var r) ? r : null);

    public Task<IReadOnlyList<TopicRecord>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TopicRecord>>([.. _items.Values]);

    public Task<IReadOnlyList<TopicRecord>> GetByStageAsync(TopicStage stage, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TopicRecord>>([.. _items.Values.Where(t => t.Stage == stage)]);

    public Task UpsertAsync(TopicRecord record, CancellationToken ct = default)
    {
        _items[record.TopicId] = record;
        return Task.CompletedTask;
    }
}

public sealed class InMemoryBlackoutRepository : IBlackoutRepository
{
    private readonly ConcurrentDictionary<string, BlackoutRecord> _items = new(StringComparer.OrdinalIgnoreCase);

    public Task<BlackoutRecord?> GetAsync(string blackoutId, CancellationToken ct = default)
        => Task.FromResult(_items.TryGetValue(blackoutId, out var r) ? r : null);

    public Task<IReadOnlyList<BlackoutRecord>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<BlackoutRecord>>([.. _items.Values]);

    public Task UpsertAsync(BlackoutRecord record, CancellationToken ct = default)
    {
        _items[record.BlackoutId] = record;
        return Task.CompletedTask;
    }
}
