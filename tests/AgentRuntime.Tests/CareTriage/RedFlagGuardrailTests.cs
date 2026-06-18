using AgentRuntime.Context;
using CareTriageAgent.Guardrails;

namespace AgentRuntime.Tests.CareTriage;

public class RedFlagGuardrailTests
{
    private static RedFlagGuardrail CardiacGuardrail() => new(new[]
    {
        new RedFlagRule(
            Id: "cardiac",
            AllOf: new[] { "chest pain", "shortness of breath" },
            Message: "Possible cardiac emergency — call your local emergency number now.")
    });

    // Slice 3 (domain): when the latest user message contains every term of a red-flag rule,
    // the guardrail short-circuits with that rule's escalation message.
    [Fact]
    public async Task Cardiac_symptom_combo_escalates()
    {
        var ctx = new WorkContext("conv-1");
        ctx.AppendUser("I've had severe chest pain and shortness of breath for an hour");

        var verdict = await CardiacGuardrail().EvaluateAsync(ctx, CancellationToken.None);

        Assert.True(verdict.ShortCircuit);
        Assert.Contains("cardiac", verdict.Message!, StringComparison.OrdinalIgnoreCase);
    }

    // A message missing one of the rule's terms must not trip the rule.
    [Fact]
    public async Task Ordinary_symptom_passes_through()
    {
        var ctx = new WorkContext("conv-1");
        ctx.AppendUser("I have a sore throat and a mild fever");

        var verdict = await CardiacGuardrail().EvaluateAsync(ctx, CancellationToken.None);

        Assert.False(verdict.ShortCircuit);
    }
}
