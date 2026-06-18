using AgentRuntime.Context;
using CareTriageAgent.Tools;

namespace AgentRuntime.Tests.CareTriage;

public class SymptomKnowledgeBaseToolTests
{
    private static SymptomKnowledgeBaseTool Tool() => new(new[]
    {
        new SymptomEntry("sore_throat", new[] { "sore throat", "throat pain" }, BaseSeverity: 1, SelfCareAdvice: "Rest and fluids."),
        new SymptomEntry("chest_pain", new[] { "chest pain" }, BaseSeverity: 7, SelfCareAdvice: "Seek prompt medical assessment."),
    });

    // Slice 6 (domain tool): scores the latest user message against the KB and returns the
    // matched symptoms' total severity plus their self-care advice.
    [Fact]
    public async Task Scores_a_matched_symptom_and_returns_its_advice()
    {
        var ctx = new WorkContext("c1");
        ctx.AppendUser("I have a sore throat since yesterday");

        var result = await Tool().ExecuteAsync(default, ctx, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, result.Output.GetProperty("score").GetInt32());
        Assert.Contains("Rest and fluids", result.Output.GetProperty("advice").GetString());
    }

    // Multiple matched symptoms sum their severities, so more/worse symptoms raise the score.
    [Fact]
    public async Task Sums_severity_across_multiple_matched_symptoms()
    {
        var ctx = new WorkContext("c1");
        ctx.AppendUser("sore throat and chest pain");

        var result = await Tool().ExecuteAsync(default, ctx, CancellationToken.None);

        Assert.Equal(8, result.Output.GetProperty("score").GetInt32());
    }

    // No KB match -> score 0 and a safe "no specific guidance" message (never throws).
    [Fact]
    public async Task Unknown_symptom_scores_zero()
    {
        var ctx = new WorkContext("c1");
        ctx.AppendUser("my left elbow feels a bit stiff");

        var result = await Tool().ExecuteAsync(default, ctx, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.Output.GetProperty("score").GetInt32());
    }
}
