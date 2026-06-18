using System.Diagnostics;
using AgentRuntime.Context;
using AgentRuntime.Failure;
using AgentRuntime.Llm;
using AgentRuntime.Observability;
using AgentRuntime.Tools;

namespace AgentRuntime.Orchestration;

/// <summary>
/// The core agent loop. Appends the user message to the <see cref="WorkContext"/>, then repeats
/// plan -> act -> observe: ask the planner for the next step, either run a tool (recording its
/// result as an observation) or finish. Tool execution goes through an <see cref="ExecutionScope"/>
/// so a failing tool degrades the turn instead of throwing. Every turn emits a trace tree
/// (triage.turn -> guardrail / agent.step -> tool) captured for observability.
/// </summary>
public sealed class AgentOrchestrator
{
    // Config-driven in a later slice; a fixed bound for now so the loop can never run forever.
    public const int MaxSteps = 6;

    private readonly ILlmClient _planner;
    private readonly ToolRegistry _tools;
    private readonly IReadOnlyList<IGuardrail> _guardrails;
    private readonly ExecutionScope _scope;

    public AgentOrchestrator(
        ILlmClient planner,
        ToolRegistry tools,
        IEnumerable<IGuardrail>? guardrails = null,
        ExecutionScope? scope = null)
    {
        _planner = planner;
        _tools = tools;
        _guardrails = (guardrails ?? Array.Empty<IGuardrail>()).ToList();
        _scope = scope ?? new ExecutionScope(maxRetries: 0);
    }

    public async Task<TurnResult> RunTurnAsync(WorkContext ctx, string userMessage, CancellationToken ct)
    {
        using var collector = new TraceCollector();

        TurnResult result;
        var traceId = default(ActivityTraceId);
        using (var turnActivity = RuntimeActivitySource.Source.StartActivity("triage.turn"))
        {
            if (turnActivity is not null)
            {
                traceId = turnActivity.TraceId;
            }

            result = await ExecuteTurnAsync(ctx, userMessage, ct);
            turnActivity?.SetTag("degraded", result.Degraded);
        }

        var trace = traceId == default ? null : collector.BuildTree(traceId);
        return result with { Trace = trace };
    }

    private async Task<TurnResult> ExecuteTurnAsync(WorkContext ctx, string userMessage, CancellationToken ct)
    {
        ctx.AppendUser(userMessage);

        // Guardrail pipeline: runs before any planning, every turn. A short-circuit ends the turn.
        foreach (var guardrail in _guardrails)
        {
            using var guardrailActivity = RuntimeActivitySource.Source.StartActivity("guardrail");
            var verdict = await guardrail.EvaluateAsync(ctx, ct);
            guardrailActivity?.SetTag("shortCircuit", verdict.ShortCircuit);

            if (verdict.ShortCircuit)
            {
                var message = verdict.Message ?? string.Empty;
                ctx.AppendAgent(message);
                return new TurnResult(message);
            }
        }

        for (var step = 1; step <= MaxSteps; step++)
        {
            using var stepActivity = RuntimeActivitySource.Source.StartActivity("agent.step");
            var decision = await _planner.PlanNextStepAsync(ctx, _tools.Descriptors(), ct);

            switch (decision)
            {
                case PlanDecision.Finish finish:
                    ctx.AppendAgent(finish.Message);
                    return new TurnResult(finish.Message, Degraded: ctx.Degraded, Result: finish.Result);

                case PlanDecision.CallTool call:
                    if (!_tools.TryGet(call.ToolName, out var tool))
                    {
                        throw new InvalidOperationException($"Unknown tool: {call.ToolName}");
                    }

                    using (var toolActivity = RuntimeActivitySource.Source.StartActivity($"tool:{call.ToolName}"))
                    {
                        // A tool failure can impact the response: retry, then degrade to a safe observation.
                        var scopeResult = await _scope.TryExecuteAsync(
                            $"tool:{call.ToolName}",
                            FailureMode.CanImpactResponse,
                            c => tool.ExecuteAsync(call.Args, ctx, c),
                            fallback: () => new ToolResult(Success: false, Output: default, Error: "tool unavailable"),
                            ct);

                        toolActivity?.SetTag("degraded", scopeResult.Degraded);

                        if (scopeResult.Degraded)
                        {
                            ctx.Degraded = true;
                        }

                        var observation = scopeResult.Value
                            ?? new ToolResult(Success: false, Output: default, Error: "tool unavailable");
                        ctx.RecordObservation(call.ToolName, observation);
                    }

                    break;

                default:
                    throw new NotSupportedException($"Unhandled plan decision: {decision.GetType().Name}");
            }
        }

        // Step budget exhausted -> safe degraded fallback (never loops forever, never crashes).
        var budgetFallback = "I wasn't able to complete this safely; please consult a professional.";
        ctx.AppendAgent(budgetFallback);
        return new TurnResult(budgetFallback, Degraded: true);
    }
}
