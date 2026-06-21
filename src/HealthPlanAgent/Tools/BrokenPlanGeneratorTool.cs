using System.Text.Json;
using AgentRuntime.Context;
using AgentRuntime.Tools;

namespace HealthPlanAgent.Tools;

/// <summary>
/// A drop-in <c>plan_generator</c> that always fails, simulating an unavailable dependency. Swapped in
/// by the host's "Break plan generator" toggle to demonstrate the runtime's retry → degrade →
/// safe-fallback behavior live: the planner returns a conservative, clearly-degraded plan, never a crash.
/// </summary>
public sealed class BrokenPlanGeneratorTool : ITool
{
    public string Name => "plan_generator";
    public string Description => "Plan generator (simulated failure).";

    public JsonElement InputSchema { get; } =
        JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

    public Task<ToolResult> ExecuteAsync(JsonElement args, WorkContext ctx, CancellationToken ct) =>
        throw new InvalidOperationException("plan generator is unavailable");
}
