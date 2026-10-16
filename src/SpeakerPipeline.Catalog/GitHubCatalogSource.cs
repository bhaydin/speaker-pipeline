using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SpeakerPipeline.Catalog;

/// <summary>Only tree, commit, and blob GETs are used against the two allowlisted repositories.</summary>
public sealed class GitHubCatalogSource(HttpClient http, IOptions<CatalogOptions> options) : ICatalogSource
{
    private string? _token;
    private DateTimeOffset _expires;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    public async Task<RepositorySnapshot> ReadAsync(string repository, CancellationToken ct = default)
    {
        if (repository is not ("bhaydin/bhaydin-talks" or "bhaydin/Presentations")) throw new ArgumentException("Repository is not allowlisted.");
        using var metadata = await GetAsync($"repos/{repository}", ct);
        var branch = metadata.RootElement.GetProperty("default_branch").GetString();
        if (string.IsNullOrWhiteSpace(branch)) throw new InvalidOperationException("Repository has no default branch.");
        using var commit = await GetAsync($"repos/{repository}/commits/{Uri.EscapeDataString(branch)}", ct);
        var sha = commit.RootElement.GetProperty("sha").GetString()!;
        using var tree = await GetAsync($"repos/{repository}/git/trees/{sha}?recursive=1", ct);
        if (tree.RootElement.TryGetProperty("truncated", out var truncated) && truncated.GetBoolean())
            throw new InvalidOperationException("GitHub tree was truncated; preserving last good catalog.");
        var files = new List<RepositoryFile>();
        foreach (var entry in tree.RootElement.GetProperty("tree").EnumerateArray())
        {
            if (entry.GetProperty("type").GetString() != "blob") continue;
            var path = entry.GetProperty("path").GetString()!;
            if (!Included(path, repository)) continue;
            var blobSha = entry.GetProperty("sha").GetString()!;
            var textFile = path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase);
            string? text = null;
            if (textFile)
            {
                if (entry.GetProperty("size").GetInt64() > 256000) throw new InvalidOperationException("Catalog source document exceeds the metadata limit.");
                using var blob = await GetAsync($"repos/{repository}/git/blobs/{blobSha}", ct);
                if (blob.RootElement.GetProperty("encoding").GetString() != "base64") throw new InvalidOperationException("Unsupported GitHub blob encoding.");
                text = Encoding.UTF8.GetString(Convert.FromBase64String(blob.RootElement.GetProperty("content").GetString()!));
            }
            files.Add(new RepositoryFile(path, blobSha, text));
            if (files.Count > 1000) throw new InvalidOperationException("Catalog metadata file budget exceeded.");
        }
        return new RepositorySnapshot(repository, sha, files);
    }

    private static bool Included(string path, string repository) => repository.EndsWith("Presentations", StringComparison.Ordinal)
        ? path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || path.EndsWith("README.md", StringComparison.OrdinalIgnoreCase) && !path.Contains("/Code/", StringComparison.OrdinalIgnoreCase)
        : path.StartsWith("talks/", StringComparison.Ordinal) && (path.EndsWith("/talk.yaml", StringComparison.Ordinal) || path.EndsWith("/content/talk.md", StringComparison.Ordinal))
          || path.StartsWith("engagements/", StringComparison.Ordinal) && (path.EndsWith("/engagement.yaml", StringComparison.Ordinal) || path.EndsWith("/sources/session-abstract.md", StringComparison.Ordinal) || path.EndsWith("/sources/session-summary.md", StringComparison.Ordinal));

    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        var cfg = options.Value.GitHub;
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_token is not null && _expires > DateTimeOffset.UtcNow.AddMinutes(2)) return _token;
            if (!long.TryParse(cfg.AppId, out _) || !long.TryParse(cfg.InstallationId, out _) || string.IsNullOrWhiteSpace(cfg.PrivateKeyPem))
                throw new InvalidOperationException("Read-only GitHub App configuration is incomplete.");
            static string B64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var now = DateTimeOffset.UtcNow;
            var header = B64(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
            var payload = B64(JsonSerializer.SerializeToUtf8Bytes(new { iat = now.AddSeconds(-60).ToUnixTimeSeconds(), exp = now.AddMinutes(9).ToUnixTimeSeconds(), iss = cfg.AppId }));
            using var rsa = RSA.Create();
            rsa.ImportFromPem(cfg.PrivateKeyPem);
            var unsigned = $"{header}.{payload}";
            var jwt = unsigned + "." + B64(rsa.SignData(Encoding.UTF8.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            using var request = new HttpRequestMessage(HttpMethod.Post, $"app/installations/{cfg.InstallationId}/access_tokens")
            {
                Content = new StringContent("{\"repositories\":[\"bhaydin-talks\",\"Presentations\"],\"permissions\":{\"contents\":\"read\",\"metadata\":\"read\"}}", Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            using var response = await http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            _token = json.RootElement.GetProperty("token").GetString()!;
            _expires = json.RootElement.GetProperty("expires_at").GetDateTimeOffset();
            return _token;
        }
        finally { _tokenLock.Release(); }
    }
}
