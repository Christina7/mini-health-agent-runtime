using AgentRuntime.Context;
using AgentRuntime.Failure;
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

    // Slice 3 (guardrail pipeline): a guardrail that short-circuits ends the turn with its
    // message and the planner is never consulted. Proves guardrails run before any planning.
    [Fact]
    public async Task Short_circuiting_guardrail_ends_turn_without_planning()
    {
        // Strict: any call to the planner would throw, so Times.Never is enforced two ways.
        var planner = new Mock<ILlmClient>(MockBehavior.Strict);

        var guardrail = new Mock<IGuardrail>();
        guardrail
            .Setup(g => g.EvaluateAsync(It.IsAny<WorkContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GuardrailVerdict(ShortCircuit: true, Message: "🚨 Possible emergency — call your local emergency number."));

        var orchestrator = new AgentOrchestrator(planner.Object, EmptyRegistry(), new[] { guardrail.Object });
        var ctx = new WorkContext("conv-1");

        var result = await orchestrator.RunTurnAsync(ctx, "severe chest pain", CancellationToken.None);

        Assert.Contains("emergency", result.Message, StringComparison.OrdinalIgnoreCase);
        planner.Verify(
            p => p.PlanNextStepAsync(It.IsAny<WorkContext>(), It.IsAny<IReadOnlyList<ToolDescriptor>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // Slice 4 (step budget): a planner that never finishes must not loop forever. The orchestrator
    // stops at its step budget and returns a safe degraded fallback instead of hanging or crashing.
    [Fact]
    public async Task Planner_that_never_finishes_stops_at_step_budget_with_degraded_fallback()
    {
        var tool = new FakeTool("loop_tool", outputJson: """{"x":1}""");
        var registry = new ToolRegistry(new ITool[] { tool });

        // Always calls the tool, never returns Finish.
        var planner = new Mock<ILlmClient>();
        planner
            .Setup(p => p.PlanNextStepAsync(
                It.IsAny<WorkContext>(),
                It.IsAny<IReadOnlyList<ToolDescriptor>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlanDecision.CallTool("loop_tool", default));

        var orchestrator = new AgentOrchestrator(planner.Object, registry);
        var ctx = new WorkContext("conv-1");

        var result = await orchestrator.RunTurnAsync(ctx, "go in circles", CancellationToken.None);

        Assert.True(result.Degraded);
        // Bounded: the tool ran exactly the step-budget number of times, never unbounded.
        Assert.Equal(AgentOrchestrator.MaxSteps, tool.CallCount);
    }

    // Slice 8 (degrade wiring): a tool that throws is retried via the ExecutionScope, then the
    // turn degrades to a safe outcome instead of throwing. The failure never escapes the orchestrator.
    [Fact]
    public async Task Tool_failure_is_retried_then_degrades_the_turn()
    {
        var failing = new ThrowingTool("symptom_kb");
        var registry = new ToolRegistry(new ITool[] { failing });
        var planner = new CallThenFinishPlanner("symptom_kb", "done");
        var scope = new ExecutionScope(maxRetries: 2);

        var orchestrator = new AgentOrchestrator(planner, registry, guardrails: null, scope: scope);
        var ctx = new WorkContext("conv-1");

        var result = await orchestrator.RunTurnAsync(ctx, "anything", CancellationToken.None);

        Assert.True(result.Degraded);
        Assert.Equal(3, failing.CallCount); // first attempt + two retries
    }
}
