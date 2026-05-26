using SpeakerPipeline.Core;

namespace SpeakerPipeline.Core.Tests;

public class ScoringDecisionTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public void ValidateScore_accepts_values_in_range(int score)
    {
        ScoringDecision.ValidateScore(score, "Fit");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(11)]
    [InlineData(100)]
    public void ValidateScore_throws_for_out_of_range(int score)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScoringDecision.ValidateScore(score, "Fit"));
    }

    [Fact]
    public void DecidedAtUtc_defaults_to_now()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var d = new ScoringDecision
        {
            EventSlug = "x",
            Recommendation = Recommendation.SubmitNow,
            Rationale = "y",
            FitScore = 8,
            EffortScore = 4,
            ConfidenceScore = 7,
        };
        Assert.True(d.DecidedAtUtc >= before);
    }
}
