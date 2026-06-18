using System.Text.Json;
using AgentRuntime.Context;
using AgentRuntime.Tools;

namespace CareTriageAgent.Tools;

/// <summary>
/// A drop-in <c>symptom_kb</c> tool that always fails, simulating a missing data file. Swapped in by
/// the hosts' "break the symptom KB" toggle to demonstrate the runtime's retry -> degrade ->
/// safe-fallback behavior live, without a crash.
/// </summary>
public sealed class BrokenSymptomTool : ITool
{
    public string Name => "symptom_kb";
    public string Description => "Symptom KB (simulated failure).";

    public JsonElement InputSchema { get; } =
        JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

    public Task<ToolResult> ExecuteAsync(JsonElement args, WorkContext ctx, CancellationToken ct) =>
        throw new InvalidOperationException("symptom KB data file is missing");
}
