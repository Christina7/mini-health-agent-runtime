namespace AgentRuntime.Observability;

/// <summary>
/// A serializable node in a turn's trace tree: a span's name, how long it took, whether it ran
/// degraded, and its child spans. The web host returns this as JSON for the browser to render; the
/// CLI prints it as text.
/// </summary>
public sealed record TraceNode(string Name, double DurationMs, bool Degraded, IReadOnlyList<TraceNode> Children)
{
    /// <summary>This node and all its descendants, depth-first.</summary>
    public IEnumerable<TraceNode> Flatten()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var descendant in child.Flatten())
            {
                yield return descendant;
            }
        }
    }
}
