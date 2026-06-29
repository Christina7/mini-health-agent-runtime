using System.Text.Json;
using AgentRuntime.Context;
using CareTriageAgent.Tools;

namespace AgentRuntime.Tests.CareTriage;

// Slice B: symptom_kb is a pure scorer. It no longer reads the user text — it sums the base
// severities of the present symptom IDs handed to it (via the presentIds argument) by the planner,
// which sources them from symptom_extractor.
public class SymptomKnowledgeBaseToolTests
{
    private static SymptomKnowledgeBaseTool Tool() => new(new[]
    {
        new SymptomEntry("sore_throat", new[] { "sore throat", "throat pain" }, BaseSeverity: 1, SelfCareAdvice: "Rest and fluids."),
        new SymptomEntry("chest_pain", new[] { "chest pain" }, BaseSeverity: 7, SelfCareAdvice: "Seek prompt medical assessment."),
    });

    private static JsonElement PresentIds(params string[] ids) =>
        JsonSerializer.SerializeToElement(new { presentIds = ids });

    // Scores a present symptom ID and returns its advice; it does not consult the user text.
    [Fact]
    public async Task Scores_a_present_symptom_and_returns_its_advice()
    {
        var result = await Tool().ExecuteAsync(PresentIds("sore_throat"), new WorkContext("c1"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, result.Output.GetProperty("score").GetInt32());
        Assert.Contains("Rest and fluids", result.Output.GetProperty("advice").GetString());
    }

    // Multiple present symptoms sum their severities, so more/worse symptoms raise the score.
    [Fact]
    public async Task Sums_severity_across_multiple_present_symptoms()
    {
        var result = await Tool().ExecuteAsync(PresentIds("sore_throat", "chest_pain"), new WorkContext("c1"), CancellationToken.None);

        Assert.Equal(8, result.Output.GetProperty("score").GetInt32());
    }

    // No present IDs (or an unknown one) -> score 0 and a safe "no specific guidance" message.
    [Fact]
    public async Task No_present_ids_scores_zero()
    {
        var result = await Tool().ExecuteAsync(PresentIds("left_elbow_stiffness"), new WorkContext("c1"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.Output.GetProperty("score").GetInt32());
    }
}
