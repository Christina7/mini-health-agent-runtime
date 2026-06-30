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
/// (the root span -> guardrail / agent.step -> tool) captured for observability. The root span
/// name is supplied by the session (e.g. "triage.turn", "plan.turn"), so each agent's traces are
/// rooted under its own name while the loop itself stays domain-agnostic.
/// </summary>
public sealed class AgentOrchestrator
{
    // Config-driven in a later slice; a fixed bound for now so the loop can never run forever.
    public const int MaxSteps = 6;

    private readonly ILlmClient _planner;
    private readonly ToolRegistry _tools;
    private readonly IReadOnlyList<IGuardrail> _guardrails;
    private readonly ExecutionScope _scope;
    private readonly string _rootSpanName;

    public AgentOrchestrator(
        ILlmClient planner,
        ToolRegistry tools,
        IEnumerable<IGuardrail>? guardrails = null,
        ExecutionScope? scope = null,
        string rootSpanName = "agent.turn")
    {
        _planner = planner;
        _tools = tools;
        _guardrails = (guardrails ?? Array.Empty<IGuardrail>()).ToList();
        _scope = scope ?? new ExecutionScope(maxRetries: 0);
        _rootSpanName = rootSpanName;
    }

    public async Task<TurnResult> RunTurnAsync(WorkContext ctx, string userMessage, CancellationToken ct)
    {
        using var collector = new TraceCollector();

        TurnResult result;
        var traceId = default(ActivityTraceId);
        using (var turnActivity = RuntimeActivitySource.Source.StartActivity(_rootSpanName))
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
        // Clear the previous turn's working state (observations + degraded flag) so this turn is
        // assessed on its own merits; conversation History carries over for multi-turn memory.
        ctx.BeginTurn();
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
                    // Label the finishing step with the planner's summary (e.g. the final urgency) so
                    // the trace's last node says what was decided instead of being an empty span.
                    stepActivity?.SetTag("summary", finish.Summary ?? "finish");
                    ctx.AppendAgent(finish.Message);
                    return new TurnResult(finish.Message, Degraded: ctx.Degraded, Result: finish.Result);

                case PlanDecision.CallTool call:
                    stepActivity?.SetTag("summary", $"call {call.ToolName}");
                    if (!_tools.TryGet(call.ToolName, out var tool))
                    {
                        ctx.Degraded = true;
                        ctx.RecordObservation(
                            call.ToolName,
                            new ToolResult(Success: false, Output: default, Error: "unknown tool"));

                        stepActivity?.SetTag("summary", $"unknown tool: {call.ToolName}");
                        stepActivity?.SetTag("degraded", true);
                        stepActivity?.SetTag("unknownTool", call.ToolName);

                        var unknownToolFallback = "I wasn't able to complete this safely; please consult a professional.";
                        ctx.AppendAgent(unknownToolFallback);
                        return new TurnResult(unknownToolFallback, Degraded: true);
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

                        // Surface the tool's own one-line summary (e.g. "score 7") in the trace.
                        if (observation.Summary is { } toolSummary)
                        {
                            toolActivity?.SetTag("summary", toolSummary);
                        }

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
