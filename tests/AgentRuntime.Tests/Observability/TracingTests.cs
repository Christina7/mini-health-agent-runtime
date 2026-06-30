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
        var registry = new ToolRegistry(new ITool[]
        {
            new SymptomExtractorTool(new KeywordSymptomExtractor(Kb)),
            new SymptomKnowledgeBaseTool(Kb),
        });
        var orchestrator = new AgentOrchestrator(new MockTriagePlanner(Policy()), registry, rootSpanName: "triage.turn");
        var ctx = new WorkContext("conv-1");

        var turn = await orchestrator.RunTurnAsync(ctx, "sore throat", CancellationToken.None);

        Assert.NotNull(turn.Trace);
        Assert.Equal("triage.turn", turn.Trace!.Name);

        var nodes = turn.Trace.Flatten().ToList();
        Assert.Contains(nodes, n => n.Name == "agent.step");
        Assert.Contains(nodes, n => n.Name == "tool:symptom_kb");
    }

    // Slice C1 (trace clarity): every span carries a one-line Label saying what it decided or
    // produced, so the tree reads as a story instead of three identical "agent.step" nodes. Each
    // agent.step is labelled by its decision (call <tool> / the final urgency), and each tool span by
    // its result (present count / score). This is what turns an opaque step list into a readable plan.
    [Fact]
    public async Task Trace_spans_are_labelled_with_their_decision_and_result()
    {
        var registry = new ToolRegistry(new ITool[]
        {
            new SymptomExtractorTool(new KeywordSymptomExtractor(Kb)),
            new SymptomKnowledgeBaseTool(Kb),
        });
        var orchestrator = new AgentOrchestrator(new MockTriagePlanner(Policy()), registry, rootSpanName: "triage.turn");
        var ctx = new WorkContext("conv-1");

        var turn = await orchestrator.RunTurnAsync(ctx, "sore throat", CancellationToken.None);

        var nodes = turn.Trace!.Flatten().ToList();

        // The three steps tell the pipeline story in order: extract -> score -> finish(urgency).
        var stepLabels = nodes.Where(n => n.Name == "agent.step").Select(n => n.Label).ToArray();
        Assert.Equal(new[] { "call symptom_extractor", "call symptom_kb", "SelfCare" }, stepLabels);

        // Tool spans report what they produced, not just that they ran.
        Assert.Equal("1 present", nodes.Single(n => n.Name == "tool:symptom_extractor").Label);
        Assert.Equal("score 1", nodes.Single(n => n.Name == "tool:symptom_kb").Label);
    }

    // A degraded turn says so in the finishing step's label, so the trace shows the safe fallback.
    [Fact]
    public async Task Degraded_turn_finish_step_is_labelled_degraded()
    {
        // Only the scorer is registered; the missing extractor forces a safe SeeGp degrade.
        var registry = new ToolRegistry(new ITool[] { new SymptomKnowledgeBaseTool(Kb) });
        var orchestrator = new AgentOrchestrator(new MockTriagePlanner(Policy()), registry, rootSpanName: "triage.turn");
        var ctx = new WorkContext("conv-1");

        var turn = await orchestrator.RunTurnAsync(ctx, "sore throat", CancellationToken.None);

        var finishStep = turn.Trace!.Flatten().Single(n => n.Name == "agent.step");
        Assert.Equal("SeeGp · degraded", finishStep.Label);
    }

    // A degraded tool call is tagged degraded in the trace, so the failure is visible in the tree.
    [Fact]
    public async Task Degraded_tool_call_is_tagged_in_the_trace()
    {
        var registry = new ToolRegistry(new ITool[]
        {
            new SymptomExtractorTool(new KeywordSymptomExtractor(Kb)),
            new ThrowingTool("symptom_kb"),
        });
        var orchestrator = new AgentOrchestrator(new MockTriagePlanner(Policy()), registry, scope: new ExecutionScope(maxRetries: 0));
        var ctx = new WorkContext("conv-1");

        var turn = await orchestrator.RunTurnAsync(ctx, "sore throat", CancellationToken.None);

        var toolSpan = turn.Trace!.Flatten().Single(n => n.Name == "tool:symptom_kb");
        Assert.True(toolSpan.Degraded);
    }
}
