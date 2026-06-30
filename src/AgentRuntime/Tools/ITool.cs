using System.Text.Json;
using AgentRuntime.Context;

namespace AgentRuntime.Tools;

/// <summary>
/// A capability the agent can invoke during a turn. A tool exposes a name, a description,
/// and the JSON schema of its input; the orchestrator runs it and feeds the result back into
/// the loop as an observation. Tools are domain-specific; the runtime only knows this contract.
/// </summary>
public interface ITool
{
    string Name { get; }
    string Description { get; }
    JsonElement InputSchema { get; }

    Task<ToolResult> ExecuteAsync(JsonElement args, WorkContext ctx, CancellationToken ct);
}

/// <summary>
/// The outcome of running a tool: a JSON output on success, or an error message. <paramref
/// name="Summary"/> is an optional one-line trace label describing what the tool produced
/// (e.g. "score 7") — observability only, never user-facing.
/// </summary>
public sealed record ToolResult(bool Success, JsonElement Output, string? Error = null, string? Summary = null);
