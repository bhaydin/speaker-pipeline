using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SpeakerPipeline.Client;
using SpeakerPipeline.Core;
using SpeakerPipeline.Migrate;

// -----------------------------------------------------------------------------
// Usage:
//   Local dev API (DevAuthenticationHandler accepts any bearer):
//     dotnet run --project tools/SpeakerPipeline.Migrate -- \
//       --source samples --api http://localhost:5080/ [--token dev] [--dry-run]
//
//   Deployed, Entra-auth API (acquire a token for the API's scope via
//   DefaultAzureCredential — az login / managed identity):
//     dotnet run --project tools/SpeakerPipeline.Migrate -- \
//       --source samples --api https://<api-host>/ \
//       --scope api://<api-client-id>/.default
//
// Reads sample-talks.json / sample-events.json / sample-submissions.json from
// --source and upserts each through the API. --scope takes precedence over
// --token; without it the static --token (default "dev") is sent.
// -----------------------------------------------------------------------------

var options = CliOptions.Parse(args);
if (options is null)
{
    return 1;
}

var jsonOpts = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    Converters = { new JsonStringEnumConverter() },
};

var configuration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?> { ["SpeakerPipelineApi:BaseUrl"] = options.ApiBaseUrl })
    .Build();

var services = new ServiceCollection();
services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));
services.AddSingleton(_ => string.IsNullOrWhiteSpace(options.Scope)
    ? SeedTokenHandler.Static(options.Token)
    : SeedTokenHandler.ForScope(new DefaultAzureCredential(), options.Scope));
services.AddSpeakerPipelineApiClient(configuration)
    .AddHttpMessageHandler<SeedTokenHandler>();

await using var provider = services.BuildServiceProvider();
var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Migrate");
var api = provider.GetRequiredService<ISpeakerPipelineApiClient>();

logger.LogInformation("Seeding from '{Source}' to '{Api}' (auth: {Auth}){DryRun}",
    options.SourceDir, options.ApiBaseUrl,
    string.IsNullOrWhiteSpace(options.Scope) ? "static token" : $"Entra scope {options.Scope}",
    options.DryRun ? " (dry run)" : "");

var talks = Load<TalkSeed>("sample-talks.json");
var events = Load<EventSeed>("sample-events.json");
var submissions = Load<SubmissionSeed>("sample-submissions.json");

var ok = 0;
var failed = 0;

// Talks first, then events, then submissions (submissions reference both).
foreach (var t in talks)
{
    await Upsert($"talk {t.RowKey}", () => api.UpsertTalkAsync(SeedMapping.ToRecord(t)));
}
foreach (var e in events)
{
    await Upsert($"event {e.RowKey}", () => api.UpsertEventAsync(SeedMapping.ToRecord(e)));
}
foreach (var s in submissions)
{
    await Upsert($"submission {s.PartitionKey}/{s.RowKey}", () => api.UpsertSubmissionAsync(SeedMapping.ToRecord(s)));
}

logger.LogInformation("Done. {Ok} upserted, {Failed} failed.", ok, failed);
return failed == 0 ? 0 : 2;

List<T> Load<T>(string fileName)
{
    var path = Path.Combine(options.SourceDir, fileName);
    if (!File.Exists(path))
    {
        logger.LogWarning("Missing seed file: {Path}", path);
        return [];
    }
    return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), jsonOpts) ?? [];
}

async Task Upsert(string label, Func<Task> action)
{
    if (options.DryRun)
    {
        logger.LogInformation("  · would upsert {Label}", label);
        ok++;
        return;
    }

    try
    {
        await action();
        ok++;
        logger.LogInformation("  + upserted {Label}", label);
    }
    catch (Exception ex)
    {
        failed++;
        logger.LogError("  ! failed {Label}: {Message}", label, ex.Message);
    }
}

internal sealed record CliOptions(string SourceDir, string ApiBaseUrl, string Token, string? Scope, bool DryRun)
{
    public static CliOptions? Parse(string[] args)
    {
        var source = "samples";
        var api = "http://localhost:5080/";
        var token = "dev";
        string? scope = null;
        var dryRun = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--source" when i + 1 < args.Length: source = args[++i]; break;
                case "--api" when i + 1 < args.Length: api = args[++i]; break;
                case "--token" when i + 1 < args.Length: token = args[++i]; break;
                case "--scope" when i + 1 < args.Length: scope = args[++i]; break;
                case "--dry-run": dryRun = true; break;
                case "-h" or "--help":
                    Console.WriteLine("Usage: --source <dir> --api <baseUrl> [--token <bearer>] [--scope <api-scope>] [--dry-run]");
                    return null;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    return null;
            }
        }

        return new CliOptions(source, api, token, scope, dryRun);
    }
}

/// <summary>
/// Adds the Authorization header. Either sends a fixed bearer string (local dev
/// against the DevAuthenticationHandler) or acquires an Entra token for a scope
/// via a <see cref="TokenCredential"/> (seeding the deployed, auth'd API),
/// caching it until shortly before expiry.
/// </summary>
internal sealed class SeedTokenHandler : DelegatingHandler
{
    private readonly string? _staticToken;
    private readonly TokenCredential? _credential;
    private readonly string? _scope;
    private AccessToken _cached;

    private SeedTokenHandler(string? staticToken, TokenCredential? credential, string? scope)
    {
        _staticToken = staticToken;
        _credential = credential;
        _scope = scope;
    }

    public static SeedTokenHandler Static(string token) => new(token, null, null);

    public static SeedTokenHandler ForScope(TokenCredential credential, string scope) => new(null, credential, scope);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = _staticToken;

        if (_credential is not null && _scope is not null)
        {
            if (_cached.ExpiresOn <= DateTimeOffset.UtcNow.AddMinutes(5))
            {
                _cached = await _credential.GetTokenAsync(new TokenRequestContext([_scope]), cancellationToken);
            }
            token = _cached.Token;
        }

        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
