namespace SpeakerPipeline.Agents.Discovery;

/// <summary>
/// Configuration bound from the "Search" section — targeted query-based
/// discovery. Provider-neutral by intent; only Google Programmable Search ships
/// today (<see cref="Provider"/> selects the concrete adapter).
/// </summary>
public sealed class SearchOptions
{
    public const string SectionName = "Search";

    /// <summary>When false, search discovery no-ops and only the watchlist runs.</summary>
    public bool Enabled { get; set; }

    /// <summary>Selects the concrete adapter, e.g. "GoogleProgrammableSearch".</summary>
    public string Provider { get; set; } = "GoogleProgrammableSearch";

    public string Endpoint { get; set; } = "https://www.googleapis.com/customsearch/v1";

    /// <summary>API key — supply from the secret store (Key Vault reference), never in code.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Programmable Search Engine ID (Google "cx").</summary>
    public string? Cx { get; set; }

    public int MaxResultsPerQuery { get; set; } = 10;

    public int MaxQueriesPerRun { get; set; } = 10;

    /// <summary>Extraction-confidence floor for search candidates (0–1); higher than the watchlist since search is noisier.</summary>
    public double MinCandidateConfidence { get; set; } = 0.65;

    /// <summary>Whether to honor robots directives when fetching candidate pages (not yet enforced; reserved for future robots.txt parsing).</summary>
    public bool RespectRobots { get; set; } = true;

    /// <summary>Targeted queries, e.g. site:sessionize.com "Call for Speakers" "Azure" "2026".</summary>
    public List<string> Queries { get; set; } = [];

    /// <summary><see cref="MinCandidateConfidence"/> mapped to the extractor's 0–10 scale.</summary>
    public int MinConfidenceScore => (int)Math.Ceiling(Math.Clamp(MinCandidateConfidence, 0, 1) * 10);
}
