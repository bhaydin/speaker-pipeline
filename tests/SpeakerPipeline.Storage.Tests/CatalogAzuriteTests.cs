using Azure;
using Azure.Data.Tables;
using SpeakerPipeline.Core;
using SpeakerPipeline.Storage;

namespace SpeakerPipeline.Storage.Tests;

public sealed class CatalogAzuriteTests
{
    [MaintenanceAzuriteFact]
    public async Task Catalog_publish_and_inbox_use_real_conditional_storage()
    {
        var connection = Environment.GetEnvironmentVariable("PIPELINE_AZURITE_CONNECTION_STRING")!;
        var service = new TableServiceClient(connection);
        Assert.True(service.Uri.IsLoopback);
        var table = service.GetTableClient("CatalogTest" + Guid.NewGuid().ToString("N"));
        await table.CreateIfNotExistsAsync();
        try
        {
            var repo = new CatalogRepository(table);
            var source = new CatalogSource("fixture", "talk.yaml", "commit", "hash");
            var original = new CatalogSnapshot
            {
                GenerationId = Guid.NewGuid().ToString("N"),
                Talks = [new() { TalkId = "one", CurrentVersionId = "v1", Title = "One", Abstract = new string('x', 50000), Source = source }],
                Versions = [new() { TalkId = "one", VersionId = "v1", Title = "One", Source = source }]
            };
            await repo.PublishAsync(new(original, null));
            Assert.Equal(original.GenerationId, (await repo.GetAsync()).GenerationId);
            Assert.Equal(50000, (await repo.GetAsync()).Talks[0].Abstract.Length);
            // Staged orphan chunks must never be visible through the published pointer.
            await table.AddEntityAsync(new TableEntity("orphan", "000000") { ["Json"] = "partial" });
            Assert.Equal(original.GenerationId, (await repo.GetAsync()).GenerationId);
            var competing = original with { GenerationId = Guid.NewGuid().ToString("N") };
            await repo.PublishAsync(new(competing, original.GenerationId));
            Assert.Equal(original.GenerationId, (await repo.GetGenerationAsync(original.GenerationId))?.GenerationId);
            Assert.Null(await repo.GetGenerationAsync(Guid.NewGuid().ToString("N")));
            var conflict = await Assert.ThrowsAsync<RequestFailedException>(() => repo.PublishAsync(new(original with { GenerationId = Guid.NewGuid().ToString("N") }, original.GenerationId)));
            Assert.Equal(412, conflict.Status);
            Assert.Equal(competing.GenerationId, (await repo.GetAsync()).GenerationId);

            Assert.True(await repo.EnqueueAsync(new("webhook-id", "push")));
            Assert.False(await repo.EnqueueAsync(new("webhook-id", "push")));
            var job = Assert.IsType<CatalogSyncJob>(await repo.ClaimAsync());
            Assert.Null(await repo.ClaimAsync());
            await Assert.ThrowsAsync<RequestFailedException>(() => repo.CompleteAsync(job.Id, new("wrong-token", true)));
            await repo.CompleteAsync(job.Id, new(job.LeaseToken, true));
            Assert.Null(await repo.ClaimAsync());
        }
        finally { await table.DeleteAsync(); }
    }
}
