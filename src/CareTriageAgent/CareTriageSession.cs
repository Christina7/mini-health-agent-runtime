using AgentRuntime.Context;
using AgentRuntime.Failure;
using AgentRuntime.Orchestration;
using AgentRuntime.Tools;
using CareTriageAgent.Config;
using CareTriageAgent.Guardrails;
using CareTriageAgent.Triage;

namespace CareTriageAgent;

/// <summary>
/// The composition root: the one place that wires the domain-agnostic runtime to the health domain
/// from the effective <see cref="CareTriageConfig"/>. Tools are registered only if config enables
/// them, but the red-flag guardrail is registered <b>unconditionally</b> — there is no config path
/// that can switch it off. That is the safety invariant: configuration cannot override safety.
/// Both the CLI and (later) the web host drive triage through this single entry point.
/// </summary>
public sealed class CareTriageSession
{
    private readonly AgentOrchestrator _orchestrator;
    private readonly WorkContext _context;

    public CareTriageSession(
        CareTriageConfig config,
        IEnumerable<ITool> candidateTools,
        IReadOnlyList<RedFlagRule> redFlagRules,
        string conversationId = "session")
    {
        // Config-driven: only enabled tools are registered.
        var enabledTools = candidateTools.Where(t => config.IsToolEnabled(t.Name)).ToList();
        var registry = new ToolRegistry(enabledTools);

        // Safety invariant: the red-flag guardrail is always on, regardless of config or flights.
        var guardrails = new IGuardrail[] { new RedFlagGuardrail(redFlagRules) };

        var policy = new TriagePolicy(new TriageThresholds(
            config.Triage.SelfCareMaxScore, config.Triage.SeeGpMaxScore, config.Triage.UrgentCareMaxScore));
        var scope = new ExecutionScope(config.Resilience.ToolMaxRetries);

        _orchestrator = new AgentOrchestrator(new MockTriagePlanner(policy), registry, guardrails, scope);
        _context = new WorkContext(conversationId);
    }

    /// <summary>The conversation state, retained across turns for multi-turn sessions.</summary>
    public WorkContext Context => _context;

    public Task<TurnResult> OnUserMessageAsync(string message, CancellationToken ct) =>
        _orchestrator.RunTurnAsync(_context, message, ct);
}
