using System.Net;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Api.Tests;

public sealed class ReadinessEndpointTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Readiness_probes_provisioned_tables_and_requires_migration_marker_for_authority(bool enabled, bool ready)
    {
        var tables = new ProbeTables(ready);
        await using var baseFactory = new TestWebApplicationFactory();
        await using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:OpportunitiesEnabled"] = enabled.ToString(), ["Storage:OpportunitiesProvisioned"] = "true",
                ["Storage:CatalogProvisioned"] = "true", ["Storage:OperationsProvisioned"] = "true",
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TableServiceClient>(); services.AddSingleton<TableServiceClient>(tables);
                services.RemoveAll<INotificationLogRepository>(); services.AddSingleton<INotificationLogRepository>(new EmptyNotifications());
            });
        });
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/health/ready")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "dev");
        Assert.Equal(enabled && !ready ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(new[] { "Catalog", "Operations", "Opportunities" }, tables.Probed.Order().ToArray());
        Assert.Equal(enabled, tables.MarkerRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Local_readiness_always_probes_operations_even_without_the_provisioned_flag(bool missingTable)
    {
        var tables = new ProbeTables(false, missingTable);
        await using var basis = new TestWebApplicationFactory();
        await using var factory = basis.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            { ["Storage:OperationsProvisioned"] = "false" }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TableServiceClient>(); services.AddSingleton<TableServiceClient>(tables);
                services.RemoveAll<INotificationLogRepository>(); services.AddSingleton<INotificationLogRepository>(new EmptyNotifications());
            });
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "dev");
        Assert.Equal(missingTable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK,
            (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Contains("Operations", tables.Probed);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Nondevelopment_readiness_rejects_an_unacknowledged_operations_prerequisite(string environment)
    {
        await using var basis = new TestWebApplicationFactory();
        await using var factory = basis.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            { ["Storage:OperationsProvisioned"] = "false" }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TableServiceClient>(); services.AddSingleton<TableServiceClient>(new ProbeTables(false));
                services.RemoveAll<INotificationLogRepository>(); services.AddSingleton<INotificationLogRepository>(new EmptyNotifications());
            });
        });
        using var client = factory.CreateClient();
        // Retain the test authentication handler while exercising the readiness
        // environment policy; production JWT validation has separate coverage.
        factory.Services.GetRequiredService<IHostEnvironment>().EnvironmentName = environment;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/health/ready")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "dev");
        var response = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Operations", await response.Content.ReadAsStringAsync());
    }

    private sealed class ProbeTables(bool ready, bool missingOperations = false) : TableServiceClient
    {
        public HashSet<string> Probed { get; } = [];
        public bool MarkerRead { get; set; }
        public override TableClient GetTableClient(string tableName) => new ProbeTable(this, tableName, ready, missingOperations);
    }
    private sealed class ProbeTable(ProbeTables owner, string name, bool ready, bool missingOperations) : TableClient
    {
        public override AsyncPageable<T> QueryAsync<T>(string? filter = null, int? maxPerPage = null, IEnumerable<string>? select = null, CancellationToken cancellationToken = default)
        {
            owner.Probed.Add(name);
            if (name == "Operations" && missingOperations) throw new RequestFailedException(404, "Synthetic table missing.");
            return AsyncPageable<T>.FromPages([Page<T>.FromValues([], null, null!)]);
        }
        public override Task<NullableResponse<T>> GetEntityIfExistsAsync<T>(string partitionKey, string rowKey, IEnumerable<string>? select = null, CancellationToken cancellationToken = default)
        {
            Assert.Equal("__migration", partitionKey); Assert.Equal("ready", rowKey);
            owner.MarkerRead = true;
            return Task.FromResult<NullableResponse<T>>(new ProbeResponse<T>(ready ? (T)(object)new TableEntity { ["SnapshotChecksum"] = "verified-checksum" } : default));
        }
    }
    private sealed class ProbeResponse<T>(T? value) : NullableResponse<T>
    {
        public override bool HasValue => value is not null;
        public override T Value => value!;
        public override Response GetRawResponse() => null!;
    }
    private sealed class EmptyNotifications : INotificationLogRepository
    {
        public Task<NotificationLogRecord?> GetAsync(string period, string id, CancellationToken ct = default) => Task.FromResult<NotificationLogRecord?>(null);
        public Task<IReadOnlyList<NotificationLogRecord>> GetForPeriodAsync(string period, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<NotificationLogRecord>>([]);
        public Task UpsertAsync(NotificationLogRecord record, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
