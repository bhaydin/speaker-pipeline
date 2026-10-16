using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SpeakerPipeline.Core;
using SpeakerPipeline.Storage;

namespace SpeakerPipeline.Storage.Tests;

public sealed class MaintenanceTargetSafetyTests
{
    // Public dummy credential for constructor-only tests; no service requests are made.
    private const string DummyKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    [Fact]
    public void Local_dev_connection_string_cannot_reach_cloud_tables()
    {
        using var services = Services(new()
        {
            ["Storage:ConnectionString"] = $"DefaultEndpointsProtocol=https;AccountName=example;AccountKey={DummyKey};TableEndpoint=https://example.table.core.windows.net",
        });
        Assert.Throws<InvalidOperationException>(() => services.GetRequiredService<TableServiceClient>());
    }

    [Fact]
    public void Local_table_connection_string_cannot_send_private_journal_to_cloud_blobs()
    {
        using var services = Services(new()
        {
            ["Storage:ConnectionString"] = $"DefaultEndpointsProtocol=http;AccountName=example;AccountKey={DummyKey};TableEndpoint=http://127.0.0.1:11002/example;BlobEndpoint=https://example.blob.core.windows.net",
        });
        Assert.Throws<InvalidOperationException>(() => services.GetRequiredService<IPipelineMaintenanceService>());
    }

    [Fact]
    public void Local_tables_cannot_override_blob_endpoint_to_cloud()
    {
        using var services = Services(new()
        {
            ["Storage:TableEndpoint"] = "http://127.0.0.1:11002/example",
            ["Maintenance:BlobEndpoint"] = "https://example.blob.core.windows.net",
        });
        Assert.Throws<InvalidOperationException>(() => services.GetRequiredService<IPipelineMaintenanceService>());
    }

    [Fact]
    public void Azurite_table_and_blob_pair_resolves_without_cloud_calls()
    {
        using var services = Services(new() { ["Storage:ConnectionString"] = "UseDevelopmentStorage=true" });
        Assert.NotNull(services.GetRequiredService<IPipelineMaintenanceService>());
    }

    private static ServiceProvider Services(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddSpeakerPipelineStorage(config, "Development");
        services.AddPipelineMaintenanceStorage(config);
        return services.BuildServiceProvider();
    }
}
