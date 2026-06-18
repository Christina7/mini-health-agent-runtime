using System.Text.Json;

namespace AgentRuntime.Orchestration;

/// <summary>
/// The outcome of one turn through the orchestrator: the user-facing reply, whether the turn ran
/// in a degraded mode (a tool failed and the runtime fell back to a safe answer), and an optional
/// structured <see cref="Result"/> payload (JSON) the app deserializes into its own result type.
/// </summary>
public sealed record TurnResult(string Message, bool Degraded = false, JsonElement? Result = null);
