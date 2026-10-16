using SpeakerPipeline.Core;

namespace SpeakerPipeline.Agents.Discovery;

/// <summary>Scoped to an invocation. Contains only stable codes, never URLs or provider response bodies.</summary>
public sealed class DiscoveryDiagnostics
{
    private readonly List<RunIssue> _issues = [];
    public void Add(string source, string code) => _issues.Add(new(code, source));
    public IReadOnlyList<RunIssue> Snapshot() => _issues.GroupBy(i => (i.Source, i.Code))
        .Select(g => new RunIssue(g.Key.Code, g.Key.Source, g.Sum(i => i.Count))).ToArray();
    public void Reset() => _issues.Clear();
}
