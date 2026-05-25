using Microsoft.Extensions.AI;

namespace SpeakerPipeline.Agents.Scoring.Tests;

/// <summary>
/// Minimal IChatClient fake. Returns the next queued response (or a default
/// canned one) and records the messages it received.
/// </summary>
public sealed class FakeChatClient : IChatClient
{
    private readonly Queue<string> _responses = new();
    public List<IList<ChatMessage>> CapturedMessages { get; } = [];

    public ChatClientMetadata Metadata { get; } = new("fake", new Uri("https://fake.invalid"), "fake-model");

    public void Enqueue(string responseJson) => _responses.Enqueue(responseJson);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = messages.ToList();
        CapturedMessages.Add(snapshot);

        var content = _responses.Count > 0
            ? _responses.Dequeue()
            : """{"eventSlug":"unknown","recommendation":"Monitor","rationale":"default fake response","fitScore":5,"effortScore":5,"confidenceScore":5,"recommendedTalkSlug":null}""";

        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, content));
        return Task.FromResult(response);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Streaming not used by the scoring agent.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
