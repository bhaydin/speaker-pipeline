using Microsoft.Extensions.AI;

namespace SpeakerPipeline.Agents.Scoring;

/// <summary>Reserves worst-case input/output before each call; failures keep their reservation.</summary>
internal sealed class ScoringRunBudget(ScoringAgentOptions options)
{
    public long InputTokens { get; private set; }
    public long CompletionTokens { get; private set; }
    public long ReportedInputTokens { get; private set; }
    public long ReportedCompletionTokens { get; private set; }
    private long _inputReservation;
    private long _outputReservation;

    public void Reserve(int inputByteBound)
    {
        if (InputTokens + inputByteBound > options.MaxEstimatedInputTokensPerRun ||
            CompletionTokens + options.MaxOutputTokens > options.MaxCompletionTokensPerRun)
            throw new ScoringBudgetException();
        _inputReservation = inputByteBound;
        _outputReservation = options.MaxOutputTokens;
        InputTokens += _inputReservation;
        CompletionTokens += _outputReservation;
    }

    public void Complete(UsageDetails? usage)
    {
        // OutputTokenCount includes reasoning; adding ReasoningTokenCount would double-count it.
        if (usage?.InputTokenCount is { } input)
        { InputTokens += input - _inputReservation; ReportedInputTokens += input; }
        if (usage?.OutputTokenCount is { } output)
        { CompletionTokens += output - _outputReservation; ReportedCompletionTokens += output; }
        _inputReservation = 0;
        _outputReservation = 0;
    }
}
