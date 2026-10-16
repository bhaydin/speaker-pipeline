namespace SpeakerPipeline.Migrate;

internal sealed record CliOptions(string Command, Uri Api, string? Source, string? Snapshot, string? Output, string? Scope, bool Apply,
    DateTimeOffset? At = null, int? PrepWindowDays = null, int? PrepThreshold = null, string? RepairsOutput = null)
{
    public const string Usage = "Commands: samples, import, export, preview, review, review-scoring, apply, restore, restore-snapshot, migrate-opportunities, recompute. Required: --api <absolute URL>. " +
        "Remote API: --scope <API scope> (Azure CLI login). samples/import: --source <directory> [--apply]. " +
        "export: --output <private snapshot.json>. preview: --source <snapshot.json> --output <private manifest.json>. " +
        "review: --source <reviewed-decisions.json> --snapshot <snapshot.json> --output <private manifest.json>. " +
        "review-scoring: --source <reviewed-scoring-decisions.json> --snapshot <snapshot.json> --output <private manifest.json> (offline proposal only). " +
        "apply/restore: --source <manifest.json> --snapshot <snapshot.json> --output <private result.json> [restore: --apply]. " +
        "restore-snapshot: --source <snapshot.json> --output <private result.json> [--apply]. " +
        "migrate-opportunities: --source <frozen snapshot.json> --output <private result.json> [--apply]. " +
        "recompute: --source <before.json> --snapshot <after.json> --at <UTC timestamp> --prep-window-days <deployed value> --prep-threshold <deployed value> --output <private report.json> --repairs-output <private manifest.json>. " +
        "All inputs and outputs must be explicit; imports preview unless --apply is supplied.";

    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("samples" or "import" or "export" or "preview" or "review" or "review-scoring" or "apply" or "restore" or "restore-snapshot" or "migrate-opportunities" or "recompute"))
            throw new ArgumentException(Usage);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var apply = false;
        for (var i = 1; i < args.Length; i++)
        {
            var key = args[i];
            if (key == "--apply")
            {
                if (apply) throw new ArgumentException("Duplicate --apply.");
                apply = true;
                continue;
            }
            if (key is not ("--api" or "--source" or "--snapshot" or "--output" or "--scope" or "--at" or "--prep-window-days" or "--prep-threshold" or "--repairs-output") || i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Unknown argument or missing value. " + Usage);
            if (!values.TryAdd(key, args[++i])) throw new ArgumentException("Duplicate argument.");
        }
        string Required(string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException($"{key} is required.");
        if (!Uri.TryCreate(Required("--api"), UriKind.Absolute, out var api) || api.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(api.UserInfo) || !string.IsNullOrEmpty(api.Query) || !string.IsNullOrEmpty(api.Fragment) || api.AbsolutePath != "/")
            throw new ArgumentException("--api must be an absolute HTTP(S) origin without credentials, path, query, or fragment.");
        var scope = values.GetValueOrDefault("--scope");
        if (!api.IsLoopback && (api.Scheme != "https" || args[0] is not ("preview" or "review" or "review-scoring" or "recompute") && string.IsNullOrWhiteSpace(scope)))
            throw new ArgumentException("Remote APIs require HTTPS and an explicit --scope; use az login for credentials.");
        if (args[0] == "samples" && !api.IsLoopback)
            throw new ArgumentException("The samples command is restricted to a loopback API.");
        var source = args[0] == "export" ? null : Required("--source");
        var snapshot = args[0] is "apply" or "restore" or "recompute" or "review" or "review-scoring" ? Required("--snapshot") : null;
        var output = args[0] is "export" or "preview" or "apply" or "restore" or "restore-snapshot" or "migrate-opportunities" or "recompute" or "review" or "review-scoring" ? Required("--output") : null;
        if (apply && args[0] is not ("samples" or "import" or "restore" or "restore-snapshot" or "migrate-opportunities")) throw new ArgumentException("--apply is only valid with imports, restores or opportunity migration.");
        var allowed = new HashSet<string> { "--api", "--scope" };
        if (source is not null) allowed.Add("--source");
        if (snapshot is not null) allowed.Add("--snapshot");
        if (output is not null) allowed.Add("--output");
        DateTimeOffset? at = null;
        int? window = null;
        int? threshold = null;
        string? repairsOutput = null;
        if (args[0] == "recompute")
        {
            allowed.UnionWith(["--at", "--prep-window-days", "--prep-threshold", "--repairs-output"]);
            var clock = Required("--at");
            if (!clock.EndsWith('Z') || !DateTimeOffset.TryParse(clock, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsedAt))
                throw new ArgumentException("--at must be an explicit UTC timestamp ending in Z.");
            if (!int.TryParse(Required("--prep-window-days"), out var parsedWindow) || parsedWindow < 0 ||
                !int.TryParse(Required("--prep-threshold"), out var parsedThreshold) || parsedThreshold < 1)
                throw new ArgumentException("Recompute requires the deployed nonnegative prep window and positive threshold.");
            at = parsedAt; window = parsedWindow; threshold = parsedThreshold;
            repairsOutput = Required("--repairs-output");
        }
        if (values.Keys.Any(k => !allowed.Contains(k))) throw new ArgumentException("An argument is not valid for this command.");
        return new(args[0], api, source, snapshot, output, scope, apply, at, window, threshold, repairsOutput);
    }
}
