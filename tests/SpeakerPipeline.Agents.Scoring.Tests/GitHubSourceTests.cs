using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Catalog;

namespace SpeakerPipeline.Agents.Scoring.Tests;

public sealed class GitHubSourceTests
{
    [Fact]
    public async Task Source_follows_current_default_branch_and_only_requests_read_permissions()
    {
        using var rsa = RSA.Create(2048);
        var calls = new List<string>();
        using var handler = new Handler(async request =>
        {
            calls.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            var path = request.RequestUri.AbsolutePath;
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/app/installations/2/access_tokens", path);
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.Equal(["bhaydin-talks", "Presentations"], json.RootElement.GetProperty("repositories").EnumerateArray().Select(x => x.GetString()));
                Assert.All(json.RootElement.GetProperty("permissions").EnumerateObject(), property => Assert.Equal("read", property.Value.GetString()));
                return Json(new { token = "synthetic-installation-token", expires_at = DateTimeOffset.UtcNow.AddMinutes(50) });
            }
            Assert.Equal(HttpMethod.Get, request.Method);
            return path switch
            {
                "/repos/bhaydin/bhaydin-talks" => Json(new { default_branch = "trunk" }),
                "/repos/bhaydin/bhaydin-talks/commits/trunk" => Json(new { sha = "current-head" }),
                "/repos/bhaydin/bhaydin-talks/git/trees/current-head" => Json(new { truncated = false, tree = new[]
                {
                    new { type = "blob", path = "talks/sample/talk.yaml", sha = "metadata", size = 50 },
                    new { type = "blob", path = "demo/secret.cs", sha = "ignored", size = 1000 },
                } }),
                "/repos/bhaydin/bhaydin-talks/git/blobs/metadata" => Json(new { encoding = "base64", content = Convert.ToBase64String(Encoding.UTF8.GetBytes("title: A fictional talk")) }),
                _ => throw new InvalidOperationException("Unexpected source request: " + path),
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        var source = new GitHubCatalogSource(http, Options.Create(new CatalogOptions
        {
            GitHub = new() { AppId = "1", InstallationId = "2", PrivateKeyPem = rsa.ExportRSAPrivateKeyPem() },
        }));
        var result = await source.ReadAsync("bhaydin/bhaydin-talks");
        Assert.Equal("current-head", result.CommitSha);
        Assert.Equal("talks/sample/talk.yaml", Assert.Single(result.Files).Path);
        Assert.Single(calls, call => call.StartsWith("POST", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, call => call.Contains("ignored", StringComparison.Ordinal));
        await Assert.ThrowsAsync<ArgumentException>(() => source.ReadAsync("example/unapproved"));
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
