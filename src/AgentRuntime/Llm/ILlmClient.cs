using AgentRuntime.Context;

namespace AgentRuntime.Llm;

/// <summary>
/// A planner: decides the agent's next step from the current <see cref="WorkContext"/>.
/// It only decides — the orchestrator executes. The offline mock and a future Anthropic
/// client are interchangeable implementations of this one interface.
/// </summary>
public interface ILlmClient
{
    Task<PlanDecision> PlanNextStepAsync(
        WorkContext ctx,
        IReadOnlyList<ToolDescriptor> tools,
        CancellationToken ct);
}
