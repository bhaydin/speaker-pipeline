using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SpeakerPipeline.Agents.Scoring;

namespace SpeakerPipeline.Hosting.Functions.Functions;

/// <summary>
/// Runs the scoring agent on a schedule. Monday 06:00 UTC by default —
/// the digest goes out Monday morning Central Time, so scoring needs to be
/// fresh by then.
/// </summary>
public sealed class ScoringAgentTimerTrigger(ScoringAgent agent, ILogger<ScoringAgentTimerTrigger> logger)
{
    [Function("ScoringAgentTimerTrigger")]
    public async Task Run([TimerTrigger("0 0 6 * * MON")] TimerInfo timer, CancellationToken ct)
    {
        logger.LogInformation("Scoring agent run starting (scheduled). Next: {Next}", timer.ScheduleStatus?.Next);
        var decisions = await agent.RunAsync(ct);
        logger.LogInformation("Scoring agent run complete. Decisions: {Count}", decisions.Count);
    }
}
