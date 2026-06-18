using AgentRuntime.Context;
using AgentRuntime.Llm;
using AgentRuntime.Tools;

namespace AgentRuntime.Orchestration;

/// <summary>
/// The core agent loop. Appends the user message to the <see cref="WorkContext"/>, then repeats
/// plan -> act -> observe: ask the planner for the next step, either run a tool (recording its
/// result as an observation) or finish. Guardrails, failure handling, and config-driven step
/// budgets are added in later slices.
/// </summary>
public sealed class AgentOrchestrator
{
    // Config-driven in a later slice; a fixed bound for now so the loop can never run forever.
    private const int MaxSteps = 6;

    private readonly ILlmClient _planner;
    private readonly ToolRegistry _tools;
    private readonly IReadOnlyList<IGuardrail> _guardrails;

    public AgentOrchestrator(ILlmClient planner, ToolRegistry tools, IEnumerable<IGuardrail>? guardrails = null)
    {
        _planner = planner;
        _tools = tools;
        _guardrails = (guardrails ?? Array.Empty<IGuardrail>()).ToList();
    }

    public async Task<TurnResult> RunTurnAsync(WorkContext ctx, string userMessage, CancellationToken ct)
    {
        ctx.AppendUser(userMessage);

        // Guardrail pipeline: runs before any planning, every turn. A short-circuit ends the turn.
        foreach (var guardrail in _guardrails)
        {
            var verdict = await guardrail.EvaluateAsync(ctx, ct);
            if (verdict.ShortCircuit)
            {
                var message = verdict.Message ?? string.Empty;
                ctx.AppendAgent(message);
                return new TurnResult(message);
            }
        }

        for (var step = 1; step <= MaxSteps; step++)
        {
            var decision = await _planner.PlanNextStepAsync(ctx, _tools.Descriptors(), ct);

            switch (decision)
            {
                case PlanDecision.Finish finish:
                    ctx.AppendAgent(finish.Message);
                    return new TurnResult(finish.Message);

                case PlanDecision.CallTool call:
                    if (!_tools.TryGet(call.ToolName, out var tool))
                    {
                        throw new InvalidOperationException($"Unknown tool: {call.ToolName}");
                    }

                    var result = await tool.ExecuteAsync(call.Args, ctx, ct);
                    ctx.RecordObservation(call.ToolName, result);
                    break;

                default:
                    throw new NotSupportedException($"Unhandled plan decision: {decision.GetType().Name}");
            }
        }

        // Step budget exhausted -> safe degraded fallback (pinned by a dedicated slice).
        var fallback = "I wasn't able to complete this safely; please consult a professional.";
        ctx.AppendAgent(fallback);
        return new TurnResult(fallback, Degraded: true);
    }
}
