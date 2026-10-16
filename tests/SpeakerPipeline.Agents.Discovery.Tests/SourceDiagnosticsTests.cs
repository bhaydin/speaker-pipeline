using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace SpeakerPipeline.Agents.Discovery.Tests;

public sealed class SourceDiagnosticsTests
{
    [Fact]
    public async Task Search_403_is_reported_without_provider_payload_or_credentials()
    {
        var diagnostics = new DiscoveryDiagnostics();
        var adapter = new GoogleProgrammableSearchAdapter(new HttpClient(new ResponseHandler(_ => new(HttpStatusCode.Forbidden)
        { Content = new StringContent("PRIVATE_RESPONSE") })), Options.Create(new SearchOptions { ApiKey = "SYNTHETIC_SECRET", Cx = "cx" }),
            NullLogger<GoogleProgrammableSearchAdapter>.Instance, diagnostics);
        Assert.Empty(await adapter.SearchAsync("query", 3));
        var issue = Assert.Single(diagnostics.Snapshot());
        Assert.Equal("http_403", issue.Code);
        Assert.DoesNotContain("SECRET", System.Text.Json.JsonSerializer.Serialize(issue));
    }

    [Fact]
    public async Task Malformed_search_response_is_an_error_while_valid_empty_result_is_not_provider_failure()
    {
        var diagnostics = new DiscoveryDiagnostics();
        var malformed = new GoogleProgrammableSearchAdapter(new HttpClient(new ResponseHandler(_ => new(HttpStatusCode.OK)
        { Content = new StringContent("broken") })), Options.Create(new SearchOptions { ApiKey = "test", Cx = "test" }),
            NullLogger<GoogleProgrammableSearchAdapter>.Instance, diagnostics);
        await malformed.SearchAsync("query", 3);
        Assert.Equal("invalid_payload", Assert.Single(diagnostics.Snapshot()).Code);
        diagnostics.Reset();
        var empty = new GoogleProgrammableSearchAdapter(new HttpClient(new ResponseHandler(_ => new(HttpStatusCode.OK)
        { Content = new StringContent("{}") })), Options.Create(new SearchOptions { ApiKey = "test", Cx = "test" }),
            NullLogger<GoogleProgrammableSearchAdapter>.Instance, diagnostics);
        await empty.SearchAsync("query", 3);
        Assert.Empty(diagnostics.Snapshot());
    }

    [Fact]
    public async Task Future_feed_404_is_expected_and_current_feed_failure_is_visible()
    {
        var diagnostics = new DiscoveryDiagnostics();
        var handler = new ResponseHandler(request => request.RequestUri!.AbsolutePath.Contains("2027", StringComparison.Ordinal)
            ? new(HttpStatusCode.NotFound) : new(HttpStatusCode.OK) { Content = new StringContent("[{\"name\":\"Example\",\"url\":\"https://example.test\",\"startDate\":\"2026-12-01\",\"country\":\"USA\"}]") });
        var source = new ConfsTechSource(new HttpClient(handler), Options.Create(new ConfsTechOptions
        { Enabled = true, Topics = ["dotnet"], IncludeNextYear = true }), new FixedTime(), NullLogger<ConfsTechSource>.Instance, diagnostics);
        await source.FetchAsync();
        Assert.Empty(diagnostics.Snapshot());
    }

    private sealed class FixedTime : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero); }
    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request)); }
}
