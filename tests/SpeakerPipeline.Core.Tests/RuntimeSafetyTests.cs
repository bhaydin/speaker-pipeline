using SpeakerPipeline.Core;

namespace SpeakerPipeline.Core.Tests;

public sealed class RuntimeSafetyTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("")]
    public void Azure_requires_production(string environment)
        => Assert.Throws<InvalidOperationException>(() => RuntimeSafety.ValidateHost(environment, true));

    [Fact]
    public void Production_host_and_local_development_are_supported()
    {
        RuntimeSafety.ValidateConsumer("Production", true, "https://api.example.org", "api://example/.default");
        RuntimeSafety.ValidateConsumer("Development", false, "http://localhost:5080", null);
        RuntimeSafety.ValidateStorage("Development", false, "UseDevelopmentStorage=true");
    }

    [Theory]
    [InlineData(null, "api://example/.default")]
    [InlineData("http://api.example.org", "api://example/.default")]
    [InlineData("https://api.example.org", null)]
    public void Production_consumers_fail_fast(string? url, string? scope)
        => Assert.Throws<InvalidOperationException>(() => RuntimeSafety.ValidateConsumer("Production", true, url, scope));

    [Fact]
    public void Development_http_is_loopback_only()
        => Assert.Throws<InvalidOperationException>(() =>
            RuntimeSafety.ValidateConsumer("Development", false, "http://api.example.org", null));

    [Fact]
    public void Production_storage_rejects_connection_strings()
        => Assert.Throws<InvalidOperationException>(() =>
            RuntimeSafety.ValidateStorage("Production", true, "UseDevelopmentStorage=true"));

    [Fact]
    public void Local_development_cannot_seed_cloud_storage()
        => Assert.Throws<InvalidOperationException>(() =>
            RuntimeSafety.ValidateStorage("Development", false, null, "https://example.table.core.windows.net"));
}
