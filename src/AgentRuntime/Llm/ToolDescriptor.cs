using System.Text.Json;

namespace AgentRuntime.Llm;

/// <summary>
/// The planner's view of a tool: what it is and the JSON schema of its input. The runtime
/// hands the enabled tools' descriptors to the planner so it can choose which to call.
/// </summary>
public sealed record ToolDescriptor(string Name, string Description, JsonElement InputSchema);
