namespace SpeakerPipeline.Agents.Discovery;

/// <summary>
/// Configuration bound from the "ConfsTech" section — the structured
/// confs.tech community feed (tech-conferences/conference-data on GitHub).
/// Because the payload is already structured, this source bypasses LLM
/// extraction entirely and maps JSON straight to <see cref="ExtractedEvent"/>.
/// </summary>
public sealed class ConfsTechOptions
{
    public const string SectionName = "ConfsTech";

    /// <summary>When false, the source no-ops (returns no candidates).</summary>
    public bool Enabled { get; set; }

    /// <summary>Raw base for topic files: <c>{Base}/{year}/{topic}.json</c>.</summary>
    public string RawBaseUrl { get; set; } =
        "https://raw.githubusercontent.com/tech-conferences/conference-data/main/conferences";

    /// <summary>Topic files to pull, e.g. "dotnet", "ai", "data", "devops".</summary>
    public List<string> Topics { get; set; } = ["dotnet", "ai"];

    /// <summary>Pull the next calendar year in addition to the current one.</summary>
    public bool IncludeNextYear { get; set; } = true;

    /// <summary>US-only geo bias: drop non-US events unless they carry an online option.</summary>
    public bool UsOnly { get; set; } = true;

    /// <summary>Country substrings treated as United States in the raw feed.</summary>
    public List<string> UsCountryAliases { get; set; } = ["U.S.A.", "USA", "United States", "US"];

    /// <summary>
    /// City/state substrings that mark a Midwest (low-travel) event. Matching
    /// candidates are surfaced first so nearby opportunities lead the digest.
    /// </summary>
    public List<string> MidwestMarkers { get; set; } =
    [
        "WI", "MN", "IL", "MI", "IN", "OH", "IA", "MO",
        "Chicago", "Milwaukee", "Madison", "Minneapolis", "Detroit",
        "Indianapolis", "Columbus", "Cleveland", "Kansas City", "St. Louis",
    ];

    /// <summary>Upper bound on candidates emitted per run — keeps a run bounded.</summary>
    public int MaxEventsPerRun { get; set; } = 100;
}
