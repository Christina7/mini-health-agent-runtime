using AgentRuntime.Config;
using AgentRuntime.Context;
using AgentRuntime.Llm;
using AgentRuntime.Orchestration;
using AgentRuntime.Tools;
using CareTriageAgent;
using CareTriageAgent.Config;
using CareTriageAgent.Guardrails;
using CareTriageAgent.Tools;

namespace AgentRuntime.Tests.Orchestration;

/// <summary>
/// The one net-new runtime change for M3: the orchestrator's root span name is parameterized so a
/// second agent can root its trace under its own name. It defaults to the domain-agnostic
/// "agent.turn"; each session passes its own. Regression: the triage session still roots at
/// "triage.turn", keeping existing traces, the web endpoint test, and the walkthrough valid.
/// </summary>
public class RootSpanNameTests
{
    private sealed class FinishingPlanner : ILlmClient
    {
        public Task<PlanDecision> PlanNextStepAsync(WorkContext ctx, IReadOnlyList<ToolDescriptor> tools, CancellationToken ct) =>
            Task.FromResult<PlanDecision>(new PlanDecision.Finish("done"));
    }

    private static AgentOrchestrator BareOrchestrator(string? rootSpanName) =>
        rootSpanName is null
            ? new AgentOrchestrator(new FinishingPlanner(), new ToolRegistry(Array.Empty<ITool>()))
            : new AgentOrchestrator(new FinishingPlanner(), new ToolRegistry(Array.Empty<ITool>()), rootSpanName: rootSpanName);

    [Fact]
    public async Task Emits_the_supplied_root_span_name()
    {
        var turn = await BareOrchestrator("plan.turn").RunTurnAsync(new WorkContext("c"), "set a goal", CancellationToken.None);

        Assert.Equal("plan.turn", turn.Trace!.Name);
    }

    [Fact]
    public async Task Defaults_to_a_domain_agnostic_agent_turn()
    {
        var turn = await BareOrchestrator(null).RunTurnAsync(new WorkContext("c"), "hello", CancellationToken.None);

        Assert.Equal("agent.turn", turn.Trace!.Name);
    }

    [Fact]
    public async Task Care_triage_session_still_roots_at_triage_turn()
    {
        const string baseJson = """
        {
          "agent":      { "maxSteps": 6, "llmProvider": "mock" },
          "tools":      { "symptom_kb": { "enabled": true } },
          "triage":     { "selfCareMaxScore": 2, "seeGpMaxScore": 5, "urgentCareMaxScore": 8 },
          "resilience": { "toolMaxRetries": 2 }
        }
        """;
        var config = new RuntimeConfigProvider(baseJson, new Dictionary<string, string>())
            .Resolve<CareTriageConfig>(Array.Empty<string>());
        var kb = new[] { new SymptomEntry("sore_throat", new[] { "sore throat" }, BaseSeverity: 1, SelfCareAdvice: "Rest.") };
        var rules = new[] { new RedFlagRule("cardiac", new[] { "chest pain" }, "Call your local emergency number.") };
        var session = new CareTriageSession(config, new ITool[] { new SymptomKnowledgeBaseTool(kb) }, rules);

        var turn = await session.OnUserMessageAsync("sore throat", CancellationToken.None);

        Assert.Equal("triage.turn", turn.Trace!.Name);
    }
}
