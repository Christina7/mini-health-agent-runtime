using AgentRuntime.Context;
using AgentRuntime.Orchestration;

namespace CareTriageAgent.Guardrails;

/// <summary>
/// A red-flag rule: if the user's message contains every term in <see cref="AllOf"/>, the
/// situation is treated as an emergency and <see cref="Message"/> is shown. (anyOf-style rules
/// are added when the rule data is loaded from JSON in a later slice.)
/// </summary>
public sealed record RedFlagRule(string Id, IReadOnlyList<string> AllOf, string Message);

/// <summary>
/// The health implementation of the runtime's <see cref="IGuardrail"/>. Runs before any planning
/// and escalates to an emergency message when the latest user message matches a red-flag rule.
/// This is registered by the app; the runtime itself knows nothing about chest pain.
/// </summary>
public sealed class RedFlagGuardrail : IGuardrail
{
    private readonly IReadOnlyList<RedFlagRule> _rules;

    public RedFlagGuardrail(IReadOnlyList<RedFlagRule> rules)
    {
        _rules = rules;
    }

    public Task<GuardrailVerdict> EvaluateAsync(WorkContext ctx, CancellationToken ct)
    {
        var text = LatestUserText(ctx);

        foreach (var rule in _rules)
        {
            var allPresent = rule.AllOf.All(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
            if (allPresent)
            {
                return Task.FromResult(GuardrailVerdict.ShortCircuitWith(rule.Message));
            }
        }

        return Task.FromResult(GuardrailVerdict.Pass);
    }

    private static string LatestUserText(WorkContext ctx)
    {
        for (var i = ctx.History.Count - 1; i >= 0; i--)
        {
            if (ctx.History[i].Role == TurnRole.User)
            {
                return ctx.History[i].Text;
            }
        }

        return string.Empty;
    }
}
