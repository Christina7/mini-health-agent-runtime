using AgentRuntime.Context;
using CareTriageAgent.Guardrails;

namespace AgentRuntime.Tests.CareTriage;

public class RedFlagGuardrailTests
{
    private static RedFlagGuardrail CardiacGuardrail() => new(new[]
    {
        new RedFlagRule(
            Id: "cardiac",
            AllOfAny: new[]
            {
                new[] { "chest pain", "chest pressure", "chest tightness" },
                new[] { "shortness of breath", "short of breath", "trouble breathing", "difficulty breathing" },
            },
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

    [Theory]
    [InlineData("I feel chest pressure and I am short of breath")]
    [InlineData("Chest tightness, plus trouble breathing")]
    [InlineData("chest-pressure with difficulty breathing")]
    public async Task Cardiac_synonym_combo_escalates(string message)
    {
        var ctx = new WorkContext("conv-1");
        ctx.AppendUser(message);

        var verdict = await CardiacGuardrail().EvaluateAsync(ctx, CancellationToken.None);

        Assert.True(verdict.ShortCircuit);
        Assert.Contains("cardiac", verdict.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("I have chest pressure but breathing is normal")]
    [InlineData("I am short of breath after running but no chest symptoms")]
    public async Task One_cardiac_group_alone_passes_through(string message)
    {
        var ctx = new WorkContext("conv-1");
        ctx.AppendUser(message);

        var verdict = await CardiacGuardrail().EvaluateAsync(ctx, CancellationToken.None);

        Assert.False(verdict.ShortCircuit);
    }
}
