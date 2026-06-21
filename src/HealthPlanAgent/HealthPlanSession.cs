using AgentRuntime.Context;
using AgentRuntime.Failure;
using AgentRuntime.Orchestration;
using AgentRuntime.Tools;
using HealthPlanAgent.Config;
using HealthPlanAgent.Guardrails;
using HealthPlanAgent.Planning;
using HealthPlanAgent.Tools;

namespace HealthPlanAgent;

/// <summary>
/// The composition root for the plan agent — the plan-side sibling of <c>CareTriageSession</c>, wiring
/// the same domain-agnostic runtime to a very different domain. It owns the session-scoped
/// <see cref="TurnInputHolder"/> (shared by the guardrail and the planner), threads its running
/// <see cref="HealthPlanResult"/> across turns so a log turn can build on the prior plan, and registers
/// <see cref="UnsafeGoalGuardrail"/> <b>unconditionally</b> — there is no config path that switches it
/// off. Tools are config-gated; safety is not. The root trace span is "plan.turn".
/// </summary>
public sealed class HealthPlanSession
{
    private readonly AgentOrchestrator _orchestrator;
    private readonly WorkContext _context;
    private readonly TurnInputHolder _holder = new();
    private HealthPlanResult? _currentPlan;

    public HealthPlanSession(HealthPlanConfig config, string conversationId = "session", bool breakPlanGenerator = false)
    {
        var policy = new PlanPolicy(config.Plan.DeficitCapFraction, config.Plan.ProteinGramsPerKg, config.Plan.CalorieFloor);

        // Config-driven: only enabled tools are registered. The broken plan generator is a demo toggle
        // for the degrade story — swap it in and the planner still produces a safe, degraded plan.
        var candidateTools = new ITool[]
        {
            new ProfileAnalyzerTool(),
            breakPlanGenerator ? new BrokenPlanGeneratorTool() : new PlanGeneratorTool(policy),
            new TaskDecomposerTool(),
            new NutritionCalculatorTool(),
            new ProgressEvaluatorTool(),
        };
        var registry = new ToolRegistry(candidateTools.Where(t => config.IsToolEnabled(t.Name)));

        // Safety invariant: the unsafe-goal guardrail is always on, regardless of config or flights.
        var guardrails = new IGuardrail[] { new UnsafeGoalGuardrail(_holder) };
        var scope = new ExecutionScope(config.Resilience.ToolMaxRetries);

        _orchestrator = new AgentOrchestrator(
            new MockHealthPlanner(_holder), registry, guardrails, scope, rootSpanName: "plan.turn");
        _context = new WorkContext(conversationId);
    }

    /// <summary>The conversation state, retained across turns for multi-turn sessions.</summary>
    public WorkContext Context => _context;

    /// <summary>The latest plan, retained across turns so a log turn builds on it. Null until created.</summary>
    public HealthPlanResult? CurrentPlan => _currentPlan;

    /// <summary>
    /// Drive one turn from the typed envelope. The envelope and the prior plan are placed in the holder
    /// (read by the guardrail and planner); a short human-readable line is written to History for the
    /// trace. The resulting plan is threaded forward — but a degraded turn never overwrites a good plan.
    /// </summary>
    public async Task<TurnResult> SubmitAsync(PlanEnvelope envelope, CancellationToken ct)
    {
        _holder.Current = envelope;
        _holder.PriorArtifact = _currentPlan;

        var turn = await _orchestrator.RunTurnAsync(_context, Summarize(envelope), ct);

        if (turn.Result is { } json)
        {
            var result = HealthPlanResult.FromJson(json);
            if (!result.Degraded || _currentPlan is null)
            {
                _currentPlan = result;
            }
        }

        return turn;
    }

    private static string Summarize(PlanEnvelope envelope) => envelope.Action switch
    {
        PlanAction.Create => envelope.Profile is { GoalWeightKg: { } goal } p
            ? $"Create plan: {envelope.Goal}, goal {goal:0.#} kg in {p.TargetDays} days."
            : $"Create plan: {envelope.Goal}.",
        PlanAction.Log => $"Log day: {envelope.Log?.CaloriesLogged} kcal, {envelope.Log?.TasksCompleted} tasks done.",
        _ => "Plan turn.",
    };
}
