using System.Text.Json;
using AgentRuntime.Context;
using AgentRuntime.Tools;

namespace AgentRuntime.Tests.Support;

/// <summary>
/// A configurable <see cref="ITool"/> test double: returns a fixed JSON output and records
/// whether it was invoked, so tests can assert the orchestrator actually ran it.
/// </summary>
public sealed class FakeTool : ITool
{
    private readonly JsonElement _output;

    public FakeTool(string name, string outputJson, string description = "fake tool")
    {
        Name = name;
        Description = description;
        // Clone() detaches the element from the JsonDocument so it stays valid after disposal.
        _output = JsonDocument.Parse(outputJson).RootElement.Clone();
        InputSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
    }

    public string Name { get; }
    public string Description { get; }
    public JsonElement InputSchema { get; }
    public int CallCount { get; private set; }
    public bool WasCalled => CallCount > 0;

    public Task<ToolResult> ExecuteAsync(JsonElement args, WorkContext ctx, CancellationToken ct)
    {
        CallCount++;
        return Task.FromResult(new ToolResult(Success: true, Output: _output));
    }
}
