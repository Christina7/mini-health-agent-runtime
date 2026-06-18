using AgentRuntime.Context;
using AgentRuntime.Llm;
using AgentRuntime.Orchestration;
using AgentRuntime.Tools;
using CareTriageAgent.Tools;
using CareTriageAgent.Triage;

namespace AgentRuntime.Tests.CareTriage;

public class MockTriagePlannerTests
{
    private static readonly SymptomEntry[] Kb =
    {
        new("sore_throat", new[] { "sore throat" }, BaseSeverity: 1, SelfCareAdvice: "Rest and fluids."),
        new("breathing_difficulty", new[] { "trouble breathing" }, BaseSeverity: 8, SelfCareAdvice: "Get assessed promptly."),
    };

    private static AgentOrchestrator Orchestrator(out WorkContext ctx)
    {
        var registry = new ToolRegistry(new ITool[] { new SymptomKnowledgeBaseTool(Kb) });
        var policy = new TriagePolicy(new TriageThresholds(SelfCareMaxScore: 2, SeeGpMaxScore: 5, UrgentCareMaxScore: 8));
        ctx = new WorkContext("conv-1");
        return new AgentOrchestrator(new MockTriagePlanner(policy), registry);
    }

    // Slice 6 (the brain): a low-severity symptom is classified SelfCare and the structured
    // TriageResult rides back on the turn, listing the tool it invoked.
    [Fact]
    public async Task Low_severity_symptom_yields_self_care_triage_result()
    {
        var orchestrator = Orchestrator(out var ctx);

        var turn = await orchestrator.RunTurnAsync(ctx, "I have a sore throat", CancellationToken.None);

        Assert.NotNull(turn.Result);
        var triage = TriageResult.FromJson(turn.Result!.Value);
        Assert.Equal(UrgencyLevel.SelfCare, triage.Urgency);
        Assert.Contains("symptom_kb", triage.ToolsInvoked);
    }

    // A high-severity symptom pushes the score past the urgent-care band into Emergency.
    [Fact]
    public async Task High_severity_symptom_yields_higher_urgency()
    {
        var orchestrator = Orchestrator(out var ctx);

        var turn = await orchestrator.RunTurnAsync(ctx, "sore throat and trouble breathing", CancellationToken.None);

        var triage = TriageResult.FromJson(turn.Result!.Value);
        Assert.Equal(UrgencyLevel.Emergency, triage.Urgency); // 1 (sore throat) + 8 (trouble breathing) = 9 > 8
    }
}
