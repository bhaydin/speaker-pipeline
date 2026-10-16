using Microsoft.Extensions.Options;
using SpeakerPipeline.Core;

namespace SpeakerPipeline.Notifications.Tests;

public sealed class PipelineHealthTests
{
    [Fact]
    public void Cost_requires_explicit_deployment_rates_and_complete_actual_usage()
    {
        var options = new NotificationOptions { ModelPricing = new() { ["deployment"] = new()
            { InputUsdPerMillionTokens = 2m, CompletionUsdPerMillionTokens = 8m } } };
        var metrics = new Dictionary<string, long> { ["input tokens"] = 500_000, ["completion tokens"] = 250_000 };
        Assert.Equal(3m, RunReporter.EstimateCost("deployment", metrics, options));
        Assert.Null(RunReporter.EstimateCost("unconfigured", metrics, options));
        Assert.Null(RunReporter.EstimateCost("deployment", metrics, null));
        metrics["usage incomplete"] = 1;
        Assert.Null(RunReporter.EstimateCost("deployment", metrics, options));
    }

    [Fact]
    public async Task Seven_day_health_totals_include_recurring_issues_and_zero_source_coverage()
    {
        var now = DateTimeOffset.Parse("2026-10-19T13:00:00Z");
        var store = new MemoryOperations();
        for (var i = 0; i < 3; i++)
        {
            var at = now.AddDays(i == 2 ? -8 : -i - 1);
            var run = new AgentRunRecord { RunId = i.ToString(), Agent = "discovery", StartedUtc = at, CompletedUtc = at,
                Status = "Degraded", Considered = 10, Changed = 2, Issues = [new("http_403", "search", 3)],
                Metrics = new Dictionary<string, long> { ["source SearchSource"] = 0, ["candidates"] = 10, ["input tokens"] = 100, ["completion tokens"] = 20 },
                ModelDeployment = "model", EstimatedCost = i == 0 ? 1m : null };
            await store.SaveAsync(OperationState.Document("runs", run.RunId, run, at));
        }
        var health = await new PipelineHealth(store, new FixedTime(now)).BuildAsync(default);
        var totals = health.Groups.Single().Items.Single(i => i.Id == "weekly-totals").Detail;
        Assert.Contains("2 runs", totals);
        Assert.Contains("candidates: 20", totals);
        Assert.Contains("input tokens: 200", totals);
        Assert.Contains("source SearchSource: 0", totals);
        Assert.Contains("in 2 runs (6 occurrences)", health.Groups.Single().Items.Single(i => i.Id == "weekly-incidents").Detail);
        Assert.Contains("Estimated model cost: unknown", health.Groups.Single().Items.Single(i => i.Id == "delivery").Detail);
        Assert.Contains(health.Groups.Single().Items, i => i.Id == "catalog");
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
