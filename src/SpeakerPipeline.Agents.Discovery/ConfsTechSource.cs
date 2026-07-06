using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Agents.Discovery;

/// <summary>
/// Structured-feed adapter over the confs.tech community dataset
/// (tech-conferences/conference-data). Pulls the configured topic files for the
/// current and next year, applies config-driven geo/date/CFP filters, and maps
/// each entry straight to a pre-extracted <see cref="ExtractedEvent"/> — no LLM,
/// no scraping. High-signal, zero-token, zero-fragility fuel for discovery.
///
/// One attempt per topic file, no retry: a file that fails to fetch is logged
/// and skipped, never a hard dependency (BUILD_PLAN §9).
/// </summary>
public sealed class ConfsTechSource(
    HttpClient http,
    IOptions<ConfsTechOptions> options,
    TimeProvider timeProvider,
    ILogger<ConfsTechSource> logger) : ISourceAdapter
{
    private readonly ConfsTechOptions _options = options.Value;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Structured feeds are trusted; extraction is exact, not inferred.</summary>
    internal const int StructuredConfidence = 9;

    public async Task<IReadOnlyList<DiscoveryCandidate>> FetchAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled || _options.Topics.Count == 0)
        {
            return [];
        }

        var now = timeProvider.GetUtcNow();
        var years = _options.IncludeNextYear
            ? new[] { now.Year, now.Year + 1 }
            : [now.Year];

        var entries = new List<ConfsTechEntry>();
        foreach (var year in years)
        {
            foreach (var topic in _options.Topics)
            {
                if (string.IsNullOrWhiteSpace(topic))
                {
                    continue;
                }

                var url = $"{_options.RawBaseUrl.TrimEnd('/')}/{year}/{topic.Trim()}.json";
                try
                {
                    using var resp = await http.GetAsync(url, ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        // A missing topic/year file (404) is normal — not every topic exists for every year.
                        logger.LogInformation("confs.tech: {Url} -> {Status} (skipped)", url, (int)resp.StatusCode);
                        continue;
                    }

                    var json = await resp.Content.ReadAsStringAsync(ct);
                    var parsed = JsonSerializer.Deserialize<List<ConfsTechEntry>>(json, JsonOpts);
                    if (parsed is not null)
                    {
                        entries.AddRange(parsed);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "confs.tech: failed to fetch/parse {Url}", url);
                }
            }
        }

        var candidates = BuildCandidates(entries, now, _options);
        logger.LogInformation("confs.tech: {Entries} entries -> {Candidates} candidates", entries.Count, candidates.Count);
        return candidates;
    }

    /// <summary>
    /// Applies the geo/date/CFP filters and maps surviving entries to candidates,
    /// Midwest-first then earliest-start. Pure and clock-injected so the whole
    /// filter/map path is unit-testable without HTTP.
    /// </summary>
    internal static IReadOnlyList<DiscoveryCandidate> BuildCandidates(
        IEnumerable<ConfsTechEntry> entries, DateTimeOffset now, ConfsTechOptions options)
    {
        var today = now.UtcDateTime.Date;
        var ranked = new List<(DiscoveryCandidate Candidate, bool Midwest, DateTime Start)>();

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.Url))
            {
                continue;
            }

            if (!TryParseDate(entry.StartDate, out var start) || start < today)
            {
                continue; // past or undated event — nothing to track
            }

            var cfpDeadline = TryParseDate(entry.CfpEndDate, out var cfp) ? cfp : (DateTime?)null;
            if (cfpDeadline is { } d && d < today)
            {
                continue; // CFP already closed
            }

            var isUs = IsUnitedStates(entry.Country, options);
            var online = entry.Online == true;
            if (options.UsOnly && !isUs && !online)
            {
                continue; // non-US and no online option — out of scope
            }

            var candidate = MapEntry(entry, start, cfpDeadline);
            var midwest = IsMidwest(entry, options);
            ranked.Add((candidate, midwest, start));
        }

        return ranked
            .OrderByDescending(x => x.Midwest)   // low-travel opportunities lead
            .ThenBy(x => x.Start)                // then soonest first
            .Take(options.MaxEventsPerRun)
            .Select(x => x.Candidate)
            .ToList();
    }

    private static DiscoveryCandidate MapEntry(ConfsTechEntry entry, DateTime start, DateTime? cfpDeadline)
    {
        var hasEnd = TryParseDate(entry.EndDate, out var end);
        var location = FormatLocation(entry);
        var online = entry.Online == true;
        var hasCity = !string.IsNullOrWhiteSpace(entry.City);
        var format = online
            ? (hasCity ? EventFormat.Hybrid : EventFormat.Virtual)
            : EventFormat.InPerson;

        // CFP status is asserted only from an observed deadline — a bare cfpUrl
        // without a date stays Unknown rather than guessing "open".
        var cfpStatus = cfpDeadline is not null ? CfpStatus.Open : CfpStatus.Unknown;

        var extracted = new ExtractedEvent
        {
            IsEvent = true,
            EventName = entry.Name!,
            EventType = EventType.Conference,
            Location = location,
            Format = format,
            EventStartDate = ToUtc(start),
            EventEndDate = hasEnd ? ToUtc(end) : null,
            CfpStatus = cfpStatus,
            CfpDeadline = cfpDeadline is { } d ? ToUtc(d) : null,
            CfpUrl = string.IsNullOrWhiteSpace(entry.CfpUrl) ? null : entry.CfpUrl,
            EventUrl = entry.Url,
            FocusFit = [], // lane fit is the scorer's call, not the feed's
            Confidence = StructuredConfidence,
        };

        // Provenance points at the CFP if one exists, else the event page.
        var url = string.IsNullOrWhiteSpace(entry.CfpUrl) ? entry.Url! : entry.CfpUrl!;
        return DiscoveryCandidate.ForExtracted(url, SourceSeenOn.ConfsTech, extracted);
    }

    private static string? FormatLocation(ConfsTechEntry entry)
    {
        var parts = new[] { entry.City, entry.Country }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        var joined = string.Join(", ", parts);
        return joined.Length == 0 ? null : joined;
    }

    private static bool IsUnitedStates(string? country, ConfsTechOptions options)
    {
        if (string.IsNullOrWhiteSpace(country))
        {
            return false;
        }

        // Normalize to alphanumerics for stable comparison (e.g. "U.S.A." -> "USA").
        var normalized = string.Concat(country.Where(char.IsLetterOrDigit)).ToUpperInvariant();

        return options.UsCountryAliases
            .Select(a => string.Concat(a.Where(char.IsLetterOrDigit)).ToUpperInvariant())
            .Any(a => normalized == a || normalized.StartsWith(a, StringComparison.Ordinal));
    }

    private static bool IsMidwest(ConfsTechEntry entry, ConfsTechOptions options)
    {
        var haystack = $"{entry.City} {entry.Country}";
        return options.MidwestMarkers.Any(marker =>
            haystack.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryParseDate(string? value, out DateTime date)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            date = parsed.Date;
            return true;
        }

        date = default;
        return false;
    }

    private static DateTimeOffset ToUtc(DateTime dateOnly) =>
        new(DateTime.SpecifyKind(dateOnly, DateTimeKind.Utc));
}

/// <summary>One entry in a confs.tech topic file. Only the fields we consume are bound.</summary>
public sealed record ConfsTechEntry
{
    public string? Name { get; init; }
    public string? Url { get; init; }
    public string? StartDate { get; init; }
    public string? EndDate { get; init; }
    public string? City { get; init; }
    public string? Country { get; init; }
    public bool? Online { get; init; }

    [JsonPropertyName("cfpUrl")]
    public string? CfpUrl { get; init; }

    [JsonPropertyName("cfpEndDate")]
    public string? CfpEndDate { get; init; }
}
