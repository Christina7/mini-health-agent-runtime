using System.Text.Json;
using AgentRuntime.Observability;

namespace AgentRuntime.Orchestration;

/// <summary>
/// The outcome of one turn through the orchestrator: the user-facing reply, whether the turn ran
/// in a degraded mode (a tool failed and the runtime fell back to a safe answer), an optional
/// structured <see cref="Result"/> payload (JSON) the app deserializes into its own result type,
/// and the turn's <see cref="Trace"/> tree.
/// </summary>
public sealed record TurnResult(string Message, bool Degraded = false, JsonElement? Result = null)
{
    /// <summary>The OpenTelemetry trace tree for this turn (conversation -> step -> tool calls).</summary>
    public TraceNode? Trace { get; init; }
}
