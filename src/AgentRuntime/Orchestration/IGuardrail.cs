using AgentRuntime.Context;

namespace AgentRuntime.Orchestration;

/// <summary>
/// A pre-planning safety hook. The orchestrator runs every registered guardrail before any
/// LLM/tool work, on every turn. A guardrail that returns a short-circuiting verdict ends the
/// turn immediately. Domain-agnostic: the runtime knows nothing about what makes a turn unsafe
/// — the health app supplies the red-flag implementation.
/// </summary>
public interface IGuardrail
{
    Task<GuardrailVerdict> EvaluateAsync(WorkContext ctx, CancellationToken ct);
}

/// <summary>
/// A guardrail's decision: pass (let the turn proceed) or short-circuit with a user-facing message.
/// </summary>
public sealed record GuardrailVerdict(bool ShortCircuit, string? Message)
{
    /// <summary>Let the turn proceed to planning.</summary>
    public static readonly GuardrailVerdict Pass = new(false, null);

    /// <summary>End the turn now and reply with <paramref name="message"/>.</summary>
    public static GuardrailVerdict ShortCircuitWith(string message) => new(true, message);
}
