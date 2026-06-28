using AgentRuntime.Context;
using AgentRuntime.Orchestration;
using System.Text.RegularExpressions;

namespace CareTriageAgent.Guardrails;

/// <summary>
/// A red-flag rule: every group in <see cref="AllOfAny"/> must match, and a group matches when any
/// phrase in that group is present. This keeps safety rules deterministic while allowing common
/// clinical wording variants such as "short of breath" vs "shortness of breath".
/// </summary>
public sealed record RedFlagRule(string Id, IReadOnlyList<IReadOnlyList<string>> AllOfAny, string Message)
{
    public RedFlagRule(string id, IReadOnlyList<string> allOf, string message)
        : this(id, allOf.Select(term => (IReadOnlyList<string>)new[] { term }).ToArray(), message)
    {
    }
}

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
        var text = Normalize(ctx.LatestUserText);

        foreach (var rule in _rules)
        {
            var allPresent = rule.AllOfAny.All(group =>
                group.Any(term => text.Contains(Normalize(term), StringComparison.Ordinal)));
            if (allPresent)
            {
                return Task.FromResult(GuardrailVerdict.ShortCircuitWith(rule.Message));
            }
        }

        return Task.FromResult(GuardrailVerdict.Pass);
    }

    private static string Normalize(string text)
    {
        var lower = text.ToLowerInvariant();
        var noPunctuation = Regex.Replace(lower, @"[^a-z0-9\s]", " ");
        return Regex.Replace(noPunctuation, @"\s+", " ").Trim();
    }
}
