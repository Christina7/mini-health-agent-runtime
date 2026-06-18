using AgentRuntime.Context;
using AgentRuntime.Failure;
using AgentRuntime.Observability;
using AgentRuntime.Orchestration;
using AgentRuntime.Tests.Support;
using AgentRuntime.Tools;
using CareTriageAgent.Tools;
using CareTriageAgent.Triage;

namespace AgentRuntime.Tests.Observability;

public class TracingTests
{
    private static readonly SymptomEntry[] Kb = { new("sore_throat", new[] { "sore throat" }, BaseSeverity: 1, SelfCareAdvice: "Rest.") };
    private static TriagePolicy Policy() => new(new TriageThresholds(2, 5, 8));

    // Slice 12: a turn emits a trace tree rooted at triage.turn, with an agent.step containing the
    // tool span — so the OpenTelemetry observability is concrete data, not just asserted.
    [Fact]
    public async Task Turn_produces_a_trace_tree_of_steps_and_tool_calls()
    {
        var registry = new ToolRegistry(new ITool[] { new SymptomKnowledgeBaseTool(Kb) });
        var orchestrator = new AgentOrchestrator(new MockTriagePlanner(Policy()), registry);
        var ctx = new WorkContext("conv-1");

        var turn = await orchestrator.RunTurnAsync(ctx, "sore throat", CancellationToken.None);

        Assert.NotNull(turn.Trace);
        Assert.Equal("triage.turn", turn.Trace!.Name);

        var nodes = turn.Trace.Flatten().ToList();
        Assert.Contains(nodes, n => n.Name == "agent.step");
        Assert.Contains(nodes, n => n.Name == "tool:symptom_kb");
    }

    // A degraded tool call is tagged degraded in the trace, so the failure is visible in the tree.
    [Fact]
    public async Task Degraded_tool_call_is_tagged_in_the_trace()
    {
        var registry = new ToolRegistry(new ITool[] { new ThrowingTool("symptom_kb") });
        var orchestrator = new AgentOrchestrator(new MockTriagePlanner(Policy()), registry, scope: new ExecutionScope(maxRetries: 0));
        var ctx = new WorkContext("conv-1");

        var turn = await orchestrator.RunTurnAsync(ctx, "sore throat", CancellationToken.None);

        var toolSpan = turn.Trace!.Flatten().Single(n => n.Name == "tool:symptom_kb");
        Assert.True(toolSpan.Degraded);
    }
}
