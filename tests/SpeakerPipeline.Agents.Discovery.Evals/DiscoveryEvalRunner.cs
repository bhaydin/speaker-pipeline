using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpeakerPipeline.Agents.Discovery;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Agents.Discovery.Evals;

/// <summary>
/// Replays recorded model outputs through the discovery extraction pipeline
/// (parse → map → slug) and checks each against a golden. Hermetic — no model
/// call — so it runs in CI and surfaces drift in field mapping, deadline
/// parsing, CFP-status classification, and non-event rejection. A previously
/// passing case that now fails is a regression, not noise.
/// </summary>
public class DiscoveryEvalRunner
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    [Fact(DisplayName = "discovery extraction eval suite — golden set")]
    public void Runs_extraction_goldens_and_writes_report()
    {
        var goldens = LoadGoldens();
        Assert.True(goldens.Cases.Count >= 5, $"Expected at least 5 golden cases, got {goldens.Cases.Count}");

        var caseReports = new List<CaseReport>();

        foreach (var c in goldens.Cases)
        {
            var extracted = DiscoveryAgent.ParseExtracted(c.ModelOutput);
            var slug = extracted.IsEvent ? DiscoveryAgent.Slugify(extracted.EventName) : string.Empty;
            var failures = new List<string>();

            if (extracted.IsEvent != c.Expected.IsEvent)
            {
                failures.Add($"isEvent: {extracted.IsEvent} != {c.Expected.IsEvent}");
            }

            if (c.Expected.IsEvent && extracted.IsEvent)
            {
                if (extracted.EventName != c.Expected.EventName)
                {
                    failures.Add($"eventName: '{extracted.EventName}' != '{c.Expected.EventName}'");
                }
                if (slug != c.Expected.Slug)
                {
                    failures.Add($"slug: '{slug}' != '{c.Expected.Slug}'");
                }
                if (extracted.CfpStatus != ParseStatus(c.Expected.CfpStatus))
                {
                    failures.Add($"cfpStatus: {extracted.CfpStatus} != {c.Expected.CfpStatus}");
                }
                if (extracted.CfpDeadline != ParseDeadline(c.Expected.CfpDeadline))
                {
                    failures.Add($"cfpDeadline: {extracted.CfpDeadline?.ToString("o") ?? "null"} != {c.Expected.CfpDeadline ?? "null"}");
                }
                if (extracted.Format != ParseFormat(c.Expected.Format))
                {
                    failures.Add($"format: {extracted.Format?.ToString() ?? "null"} != {c.Expected.Format ?? "null"}");
                }
            }

            caseReports.Add(new CaseReport(c.Id, failures.Count == 0, failures));
        }

        WriteReport(caseReports);

        var failed = caseReports.Where(r => !r.Passed).ToList();
        if (failed.Count > 0)
        {
            var detail = string.Join("\n  ", failed.Select(f => $"{f.Id}: {string.Join(", ", f.Failures)}"));
            Assert.Fail($"Discovery eval failures ({failed.Count}/{caseReports.Count}):\n  {detail}\nReport written to discovery-evals-report.json");
        }
    }

    private static CfpStatus ParseStatus(string s) => Enum.Parse<CfpStatus>(s, ignoreCase: true);

    private static EventFormat? ParseFormat(string? s) => s is null ? null : Enum.Parse<EventFormat>(s, ignoreCase: true);

    private static DateTimeOffset? ParseDeadline(string? s) =>
        s is null ? null : DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static GoldenSet LoadGoldens()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Goldens", "goldens.json");
        return JsonSerializer.Deserialize<GoldenSet>(File.ReadAllText(path), JsonOpts)
               ?? throw new InvalidOperationException("goldens.json deserialized to null.");
    }

    private static void WriteReport(IReadOnlyList<CaseReport> cases)
    {
        var report = new
        {
            RunId = Guid.NewGuid().ToString("n"),
            Total = cases.Count,
            Passed = cases.Count(c => c.Passed),
            Failed = cases.Count(c => !c.Passed),
            Cases = cases,
        };
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "discovery-evals-report.json"),
            JsonSerializer.Serialize(report, JsonOpts));
    }

    private sealed record GoldenSet
    {
        public string Version { get; init; } = "v1";
        public List<GoldenCase> Cases { get; init; } = [];
    }

    private sealed record GoldenCase
    {
        public string Id { get; init; } = string.Empty;
        public string? Note { get; init; }
        public string ModelOutput { get; init; } = string.Empty;
        public ExpectedEvent Expected { get; init; } = new();
    }

    private sealed record ExpectedEvent
    {
        public bool IsEvent { get; init; }
        public string EventName { get; init; } = string.Empty;
        public string Slug { get; init; } = string.Empty;
        public string CfpStatus { get; init; } = "Unknown";
        public string? CfpDeadline { get; init; }
        public string? Format { get; init; }
    }

    private sealed record CaseReport(string Id, bool Passed, IReadOnlyList<string> Failures);
}
