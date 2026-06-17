using AgentRuntime.Llm;

namespace AgentRuntime.Tools;

/// <summary>
/// Holds the tools available to the agent, keyed by name. Exposes their descriptors to the
/// planner and resolves a tool by name for execution. Config-based filtering (only enabled
/// tools) is layered on in a later slice.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools;

    public ToolRegistry(IEnumerable<ITool> tools)
    {
        _tools = tools.ToDictionary(t => t.Name);
    }

    public bool TryGet(string name, out ITool tool) => _tools.TryGetValue(name, out tool!);

    public IReadOnlyList<ToolDescriptor> Descriptors() =>
        _tools.Values
            .Select(t => new ToolDescriptor(t.Name, t.Description, t.InputSchema))
            .ToList();
}
