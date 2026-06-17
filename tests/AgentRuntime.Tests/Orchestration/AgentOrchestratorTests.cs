using AgentRuntime.Context;
using AgentRuntime.Llm;
using AgentRuntime.Orchestration;
using Moq;

namespace AgentRuntime.Tests.Orchestration;

public class AgentOrchestratorTests
{
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

        var orchestrator = new AgentOrchestrator(planner.Object);
        var ctx = new WorkContext("conv-1");

        var result = await orchestrator.RunTurnAsync(ctx, "sore throat", CancellationToken.None);

        Assert.Equal("Looks self-manageable; see a GP if it persists.", result.Message);
    }
}
