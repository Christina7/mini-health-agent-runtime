using AgentRuntime.Context;
using AgentRuntime.Llm;

namespace AgentRuntime.Orchestration;

/// <summary>
/// The core agent loop. Appends the user message to the <see cref="WorkContext"/>, asks the
/// planner for the next step, and acts on it. Right now it handles only <see cref="PlanDecision.Finish"/>;
/// the act-observe loop, guardrails, tools, and failure handling are added in later slices.
/// </summary>
public sealed class AgentOrchestrator
{
    private readonly ILlmClient _planner;

    public AgentOrchestrator(ILlmClient planner)
    {
        _planner = planner;
    }

    public async Task<TurnResult> RunTurnAsync(WorkContext ctx, string userMessage, CancellationToken ct)
    {
        ctx.AppendUser(userMessage);

        var decision = await _planner.PlanNextStepAsync(ctx, Array.Empty<ToolDescriptor>(), ct);

        switch (decision)
        {
            case PlanDecision.Finish finish:
                ctx.AppendAgent(finish.Message);
                return new TurnResult(finish.Message);

            default:
                throw new NotSupportedException($"Unhandled plan decision: {decision.GetType().Name}");
        }
    }
}
