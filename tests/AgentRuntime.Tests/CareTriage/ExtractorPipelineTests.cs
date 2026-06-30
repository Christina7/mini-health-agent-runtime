using System.Text.Json;
using AgentRuntime.Context;
using AgentRuntime.Orchestration;
using AgentRuntime.Tools;
using CareTriageAgent.Tools;
using CareTriageAgent.Triage;

namespace AgentRuntime.Tests.CareTriage;

// Slice B: triage is a two-step pipeline — symptom_extractor finds which known symptoms are present,
// then symptom_kb scores those IDs. These tests pin the extractor, the tool wrapper, the planner's
// extract -> score ordering, and the disabled-tool matrix (a disabled stage must degrade safely to
// SeeGp, never silently fall to SelfCare — the #36 unsafe direction).
public class ExtractorPipelineTests
{
    private static readonly SymptomEntry[] Kb =
    {
        new("sore_throat", new[] { "sore throat" }, BaseSeverity: 1, SelfCareAdvice: "Rest and fluids."),
        new("chest_pain", new[] { "chest pain", "chest pressure" }, BaseSeverity: 7, SelfCareAdvice: "Get assessed promptly."),
    };

    private static TriagePolicy Policy() => new(new TriageThresholds(2, 5, 8));

    [Fact]
    public async Task Keyword_extractor_marks_matched_symptoms_present()
    {
        var extraction = await new KeywordSymptomExtractor(Kb).ExtractAsync("I have chest pressure", CancellationToken.None);

        Assert.Equal("keyword", extraction.Provider);
        Assert.False(extraction.Fallback);
        Assert.Equal(new[] { "chest_pain" }, extraction.Symptoms.Where(s => s.Present).Select(s => s.Id));
    }

    [Fact]
    public async Task Extractor_tool_serializes_present_symptoms_to_output()
    {
        var ctx = new WorkContext("c1");
        ctx.AppendUser("sore throat and chest pain");

        var result = await new SymptomExtractorTool(new KeywordSymptomExtractor(Kb)).ExecuteAsync(default, ctx, CancellationToken.None);

        Assert.True(result.Success);
        var ids = result.Output.GetProperty("symptoms").EnumerateArray().Select(s => s.GetProperty("id").GetString());
        Assert.Equal(new[] { "sore_throat", "chest_pain" }, ids);
        Assert.False(result.Output.GetProperty("fallback").GetBoolean());
    }

    // The planner runs the stages in order — extractor first, then the scorer — and a normal symptom
    // is classified from the resulting score.
    [Fact]
    public async Task Planner_runs_extractor_then_scorer_in_order()
    {
        var orchestrator = PipelineOrchestrator();
        var ctx = new WorkContext("conv-1");

        var turn = await orchestrator.RunTurnAsync(ctx, "I have chest pressure", CancellationToken.None);

        var triage = TriageResult.FromJson(turn.Result!.Value);
        Assert.Equal(new[] { "symptom_extractor", "symptom_kb" }, triage.ToolsInvoked);
        Assert.Equal(UrgencyLevel.UrgentCare, triage.Urgency); // chest_pain severity 7 -> UrgentCare
        Assert.False(turn.Degraded);
    }

    // A pipeline that genuinely runs and finds nothing is SelfCare — distinct from "couldn't assess".
    [Fact]
    public async Task Assessed_but_nothing_recognised_is_self_care_not_degraded()
    {
        var orchestrator = PipelineOrchestrator();
        var ctx = new WorkContext("conv-1");

        var turn = await orchestrator.RunTurnAsync(ctx, "my left elbow feels a bit stiff", CancellationToken.None);

        var triage = TriageResult.FromJson(turn.Result!.Value);
        Assert.Equal(UrgencyLevel.SelfCare, triage.Urgency);
        Assert.False(triage.Degraded);
    }

    // Disabled-tool matrix: with the extractor unavailable, the turn cannot be assessed, so it must
    // degrade to SeeGp — NOT score 0 and assert SelfCare (the unsafe #36 direction).
    [Fact]
    public async Task Missing_extractor_degrades_to_see_gp_never_self_care()
    {
        // Only the scorer is registered; the extractor is "disabled".
        var registry = new ToolRegistry(new ITool[] { new SymptomKnowledgeBaseTool(Kb) });
        var orchestrator = new AgentOrchestrator(new MockTriagePlanner(Policy()), registry);
        var ctx = new WorkContext("conv-1");

        var turn = await orchestrator.RunTurnAsync(ctx, "I have chest pressure", CancellationToken.None);

        var triage = TriageResult.FromJson(turn.Result!.Value);
        Assert.Equal(UrgencyLevel.SeeGp, triage.Urgency);
        Assert.True(triage.Degraded);
        Assert.Empty(triage.ToolsInvoked); // nothing ran; we refused to fabricate a score
    }

    private static AgentOrchestrator PipelineOrchestrator()
    {
        var registry = new ToolRegistry(new ITool[]
        {
            new SymptomExtractorTool(new KeywordSymptomExtractor(Kb)),
            new SymptomKnowledgeBaseTool(Kb),
        });
        return new AgentOrchestrator(new MockTriagePlanner(Policy()), registry);
    }
}
