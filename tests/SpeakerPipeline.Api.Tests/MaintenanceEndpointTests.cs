using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Api.Tests;

public sealed class MaintenanceEndpointTests
{
    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("dev", HttpStatusCode.Forbidden)]
    public async Task Admin_requires_operator_policy(string? token, HttpStatusCode expected)
    {
        using var factory = new Factory(enabled: true, frozen: false);
        using var client = Client(factory, token);
        Assert.Equal(expected, (await client.GetAsync("/v1/admin/data/snapshot")).StatusCode);
        Assert.Equal(0, factory.Service.Calls);
    }

    [Fact]
    public async Task Admin_is_disabled_by_default()
    {
        using var factory = new Factory(enabled: false, frozen: false);
        using var client = Client(factory, "maintenance");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/v1/admin/data/snapshot")).StatusCode);
        Assert.Equal(0, factory.Service.Calls);
    }

    [Fact]
    public async Task Export_and_preview_are_available_without_freeze_but_apply_is_not()
    {
        using var factory = new Factory(enabled: true, frozen: false);
        using var client = Client(factory, "maintenance");
        var response = await client.GetAsync("/v1/admin/data/snapshot");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await response.Content.ReadFromJsonAsync<MaintenanceSnapshot>())!.WritersFrozen);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/v1/admin/data/cleanup", new MaintenanceApplyRequest { Plan = Plan() })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/v1/admin/data/cleanup", new MaintenanceApplyRequest { Plan = Plan(), Apply = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/v1/admin/data/restore", new MaintenanceRestoreRequest { OperationId = "cleanup", Apply = true })).StatusCode);
        Assert.Equal(2, factory.Service.Calls);
        Assert.Equal(0, factory.Service.Applies);
    }

    [Fact]
    public async Task Freeze_blocks_business_writes_and_allows_authorized_admin_apply()
    {
        using var factory = new Factory(enabled: true, frozen: true);
        using var client = Client(factory, "maintenance");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/v1/events", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/v1/admin/data/cleanup", new MaintenanceApplyRequest { Plan = Plan(), Apply = true })).StatusCode);
        Assert.Equal(1, factory.Service.Applies);
    }

    private static HttpClient Client(Factory factory, string? token)
    {
        var client = factory.CreateClient();
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
    private static MaintenancePlan Plan() => new()
    {
        OperationId = "cleanup", Snapshot = new() { SnapshotId = "snapshot", Target = new() { TableEndpoint = "http://local" } },
    };

    private sealed class Factory(bool enabled, bool frozen) : WebApplicationFactory<Program>
    {
        public StubMaintenance Service { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Maintenance:Enabled"] = enabled.ToString(), ["Maintenance:FreezeWrites"] = frozen.ToString(),
                ["Storage:TableEndpoint"] = "https://test.example.org", ["Storage:ConnectionString"] = "",
                ["ApplicationInsights:ConnectionString"] = "",
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPipelineMaintenanceService>();
                services.AddSingleton<IPipelineMaintenanceService>(Service);
            });
        }
    }

    private sealed class StubMaintenance : IPipelineMaintenanceService
    {
        public int Calls { get; private set; }
        public int Applies { get; private set; }
        public Task<MaintenanceSnapshot> ExportAsync(bool writersFrozen, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Plan().Snapshot with { WritersFrozen = writersFrozen });
        }
        public Task<MaintenanceResult> CleanupAsync(MaintenanceApplyRequest request, CancellationToken ct = default)
        {
            Calls++;
            if (request.Apply) Applies++;
            return Task.FromResult(new MaintenanceResult { OperationId = request.Plan.OperationId, SnapshotChecksum = "checksum", Status = request.Apply ? "Completed" : "Preview" });
        }
        public Task<MaintenanceResult> RestoreAsync(MaintenanceRestoreRequest request, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<MaintenanceResult> RestoreSnapshotAsync(MaintenanceSnapshotRestoreRequest request, bool allowLocalRehearsal = false, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<MaintenanceResult?> GetOperationAsync(string operationId, CancellationToken ct = default)
            => Task.FromResult<MaintenanceResult?>(null);
    }
}
