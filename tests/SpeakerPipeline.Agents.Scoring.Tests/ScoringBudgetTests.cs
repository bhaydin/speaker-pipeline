using Microsoft.Extensions.AI;

namespace SpeakerPipeline.Agents.Scoring.Tests;

public sealed class ScoringBudgetTests
{
    [Fact]
    public void Reservations_block_calls_before_budget_overrun_and_failed_calls_remain_charged()
    {
        var budget = new ScoringRunBudget(new() { MaxEstimatedInputTokensPerRun = 1000, MaxCompletionTokensPerRun = 100, MaxOutputTokens = 50 });
        budget.Reserve(600);
        Assert.Throws<ScoringBudgetException>(() => budget.Reserve(600));
        Assert.Equal(600, budget.InputTokens);
        Assert.Equal(50, budget.CompletionTokens);
    }

    [Fact]
    public void Actual_usage_releases_reservation_without_double_counting_reasoning()
    {
        var budget = new ScoringRunBudget(new() { MaxEstimatedInputTokensPerRun = 1000, MaxCompletionTokensPerRun = 100, MaxOutputTokens = 50 });
        budget.Reserve(600);
        budget.Complete(new UsageDetails { InputTokenCount = 100, OutputTokenCount = 30, ReasoningTokenCount = 20 });
        Assert.Equal(100, budget.InputTokens);
        Assert.Equal(30, budget.CompletionTokens);
        Assert.Equal(30, budget.ReportedCompletionTokens);
        budget.Reserve(600);
        Assert.Equal(700, budget.InputTokens);
        Assert.Equal(80, budget.CompletionTokens);
    }
}
