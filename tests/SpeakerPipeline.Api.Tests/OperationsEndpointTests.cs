using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Api.Tests;

public sealed class OperationsEndpointTests
{
    [Fact]
    public async Task Immutable_message_chunk_create_requires_auth_and_cannot_overwrite_an_existing_chunk()
    {
        await using var baseFactory = new TestWebApplicationFactory();
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        { services.RemoveAll<IOperationsRepository>(); services.AddSingleton<IOperationsRepository>(new Store()); }));
        using var client = factory.CreateClient();
        var chunk = new OperationDocument("messagechunks", "synthetic", "{\"Text\":\"HTML &amp; emoji\"}", DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync("/v1/operations/messagechunks/synthetic", chunk)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "dev");
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/v1/operations/messagechunks/synthetic", chunk)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/v1/operations/messagechunks/synthetic", chunk with { Payload = "{}" })).StatusCode);
        var stored = await client.GetFromJsonAsync<VersionedOperation>("/v1/operations/messagechunks/synthetic");
        Assert.Equal(chunk.Payload, stored!.Document.Payload);
    }

    private sealed class Store : IOperationsRepository
    {
        private VersionedOperation? _value;
        public Task<VersionedOperation?> GetAsync(string kind, string id, CancellationToken ct = default) => Task.FromResult(_value);
        public Task<IReadOnlyList<VersionedOperation>> ListAsync(string kind, DateTimeOffset since, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<VersionedOperation?> SaveAsync(OperationDocument document, string? expectedVersion = null, CancellationToken ct = default)
        {
            if (_value?.Version != expectedVersion) return Task.FromResult<VersionedOperation?>(null);
            _value = new(document, "version-1");
            return Task.FromResult<VersionedOperation?>(_value);
        }
    }
}
