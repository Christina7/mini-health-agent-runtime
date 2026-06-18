using System.Text.Json;
using AgentRuntime.Context;
using AgentRuntime.Tools;

namespace AgentRuntime.Tests.Support;

/// <summary>An <see cref="ITool"/> that always throws, and counts how many times it was invoked.</summary>
public sealed class ThrowingTool : ITool
{
    public ThrowingTool(string name)
    {
        Name = name;
    }

    public string Name { get; }
    public string Description => "always throws";
    public JsonElement InputSchema { get; } = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
    public int CallCount { get; private set; }

    public Task<ToolResult> ExecuteAsync(JsonElement args, WorkContext ctx, CancellationToken ct)
    {
        CallCount++;
        throw new InvalidOperationException("tool is broken");
    }
}
