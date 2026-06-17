using AgentRuntime.Context;
using AgentRuntime.Llm;
using AgentRuntime.Orchestration;
using AgentRuntime.Tests.Support;
using AgentRuntime.Tools;
using Moq;

namespace AgentRuntime.Tests.Orchestration;

public class AgentOrchestratorTests
{
    private static ToolRegistry EmptyRegistry() => new(Array.Empty<ITool>());

    // Slice 1 (tracer bullet): a planner that decides Finish makes the orchestrator
    // produce a reply carrying that message. Proves the core path wires together:
    // user message -> orchestrator -> planner (ILlmClient) -> reply.
    [Fact]
    public async Task Finish_decision_produces_reply_with_its_message()
    {
        var planner = new Mock<ILlmClient>();
        planner
            .Setup(p => p.PlanNextStepAsync(
                It.IsAny<WorkContext>(),
                It.IsAny<IReadOnlyList<ToolDescriptor>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlanDecision.Finish("Looks self-manageable; see a GP if it persists."));

        var orchestrator = new AgentOrchestrator(planner.Object, EmptyRegistry());
        var ctx = new WorkContext("conv-1");

        var result = await orchestrator.RunTurnAsync(ctx, "sore throat", CancellationToken.None);

        Assert.Equal("Looks self-manageable; see a GP if it persists.", result.Message);
    }

    // Slice 2 (act -> observe loop): a planner first calls a tool; the orchestrator runs it,
    // records the observation into WorkContext, and the planner's NEXT step reads that
    // observation to finish. Proves tools run AND their output is fed back into the loop.
    [Fact]
    public async Task CallTool_runs_the_tool_and_feeds_its_observation_back_before_finishing()
    {
        var symptomKb = new FakeTool("symptom_kb", outputJson: """{"advice":"rest and fluids"}""");
        var registry = new ToolRegistry(new ITool[] { symptomKb });
        var planner = new ObserveThenFinishPlanner(toolToCall: "symptom_kb", adviceKey: "advice");

        var orchestrator = new AgentOrchestrator(planner, registry);
        var ctx = new WorkContext("conv-1");

        var result = await orchestrator.RunTurnAsync(ctx, "sore throat", CancellationToken.None);

        Assert.True(symptomKb.WasCalled);
        Assert.Contains("rest and fluids", result.Message);
    }
}
