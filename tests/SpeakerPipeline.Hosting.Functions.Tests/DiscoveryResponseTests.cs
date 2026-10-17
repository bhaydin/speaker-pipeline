using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Agents.Discovery;
using SpeakerPipeline.Hosting.Functions.Functions;

namespace SpeakerPipeline.Hosting.Functions.Tests;

public sealed class DiscoveryResponseTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Worker_serializer_preserves_counts_arrays_and_funnel(bool camelCase, bool empty)
    {
        // Use the same worker configuration and serializer resolved by WriteAsJsonAsync.
        // Its case-insensitive property metadata rejects changed/Changed even without
        // a naming policy; also cover a host that explicitly selects camelCase.
        using var host = new HostBuilder().ConfigureFunctionsWorkerDefaults()
            .ConfigureServices(services =>
            {
                if (camelCase) services.Configure<JsonSerializerOptions>(options =>
                    options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
            }).Build();
        var serializer = host.Services.GetRequiredService<IOptions<WorkerOptions>>().Value.Serializer!;
        var changed = empty ? Array.Empty<DiscoveryResult>() :
            [new DiscoveryResult("pine-tech", "Pine Tech", true, "new"),
             new DiscoveryResult("lake-tech", "Lake Tech", false, "CFP updated")];
        var quarantined = empty ? Array.Empty<DiscoveryResult>() :
            [new DiscoveryResult("unconfirmed-tech", "Unconfirmed Tech", true, "needs review")];
        var report = new DiscoveryRunReport(changed, quarantined,
            new DiscoveryFunnel { Targets = changed.Length + quarantined.Length, New = empty ? 0 : 1,
                Updated = empty ? 0 : 1, Quarantined = quarantined.Length });
        var body = DiscoveryHttpTrigger.CreateResponseBody(report);
        using var stream = new MemoryStream();

        await serializer.SerializeAsync(stream, body, typeof(object), CancellationToken.None);

        stream.Position = 0;
        using var json = await JsonDocument.ParseAsync(stream);
        var root = json.RootElement;
        Assert.Equal(5, root.EnumerateObject().Count());
        Assert.Equal(changed.Length, root.GetProperty("changedCount").GetInt32());
        Assert.Equal(quarantined.Length, root.GetProperty("quarantinedCount").GetInt32());
        var changedJson = root.GetProperty(camelCase ? "changed" : "Changed");
        var quarantinedJson = root.GetProperty(camelCase ? "quarantined" : "Quarantined");
        Assert.Equal(changed.Length, changedJson.GetArrayLength());
        Assert.Equal(quarantined.Length, quarantinedJson.GetArrayLength());
        Assert.Equal(changed.Length + quarantined.Length,
            root.GetProperty(camelCase ? "funnel" : "Funnel").GetProperty(camelCase ? "targets" : "Targets").GetInt32());
        if (!empty)
        {
            Assert.Equal("pine-tech", changedJson[0].GetProperty(camelCase ? "slug" : "Slug").GetString());
            Assert.Equal("needs review", quarantinedJson[0].GetProperty(camelCase ? "changeSummary" : "ChangeSummary").GetString());
        }
    }
}
