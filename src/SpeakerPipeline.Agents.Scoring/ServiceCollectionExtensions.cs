using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SpeakerPipeline.Agents.Scoring;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddScoringAgent(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ScoringAgentOptions>()
            .Bind(configuration.GetSection(ScoringAgentOptions.SectionName))
            .PostConfigure(o => o.UseOpportunityState = configuration.GetValue<bool>("Pipeline:OpportunitiesEnabled"))
            .Validate(o => o.MaxCandidatesPerRun is > 0 and <= 20 && o.MaxCatalogTalksPerCandidate is > 0 and <= 5 &&
                o.MaxFeedbackExamples is >= 0 and <= 8 && o.MaxInputCharactersPerCandidate is >= 6000 and <= 48000 &&
                o.MaxOutputTokens is >= 300 and <= 4096 && o.MaxEstimatedInputTokensPerRun is > 0 and <= 120000 && o.MaxCompletionTokensPerRun is > 0 and <= 81920,
                "Scoring candidate, context and token budgets must be bounded.")
            .ValidateOnStart();

        services.AddSingleton<ScoringAgent>();
        return services;
    }
}
