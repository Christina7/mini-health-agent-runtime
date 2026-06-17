using AgentRuntime.Context;
using AgentRuntime.Llm;
using AgentRuntime.Orchestration;

// Slice 1 placeholder host: drives the real AgentOrchestrator with a trivial inline planner
// so the runtime is visibly runnable end-to-end. Later slices replace this with the
// CareTriageAgent domain (red-flag guardrail, symptom KB / clinic-finder tools, mock planner).

var message = args.Length > 0
    ? string.Join(' ', args)
    : "sore throat and mild fever since yesterday";

var planner = new EchoPlanner();
var orchestrator = new AgentOrchestrator(planner);
var ctx = new WorkContext(conversationId: "cli-session");

Console.WriteLine($"> {message}");
var result = await orchestrator.RunTurnAsync(ctx, message, CancellationToken.None);
Console.WriteLine($"Agent: {result.Message}");
Console.WriteLine("        ⚠ Educational only — not medical advice.");

// A stand-in planner: always finishes with a canned reply. Slice 1 only proves the path runs.
internal sealed class EchoPlanner : ILlmClient
{
    public Task<PlanDecision> PlanNextStepAsync(
        WorkContext ctx, IReadOnlyList<ToolDescriptor> tools, CancellationToken ct)
    {
        return Task.FromResult<PlanDecision>(
            new PlanDecision.Finish("Looks self-manageable; see a GP if it persists beyond a few days or worsens."));
    }
}
