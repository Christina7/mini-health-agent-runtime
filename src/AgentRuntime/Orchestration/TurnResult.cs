namespace AgentRuntime.Orchestration;

/// <summary>
/// The outcome of one turn through the orchestrator: the user-facing reply and whether the
/// turn ran in a degraded mode (a tool failed and the runtime fell back to a safe answer).
/// The <see cref="Degraded"/> flag is wired up in a later slice.
/// </summary>
public sealed record TurnResult(string Message, bool Degraded = false);
