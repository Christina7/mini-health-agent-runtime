using System.Text.Json;
using AgentRuntime.Context;
using AgentRuntime.Llm;
using AgentRuntime.Orchestration;
using AgentRuntime.Tools;

// Slice 1-2 placeholder host: drives the real AgentOrchestrator with a stand-in planner and a
// stand-in tool so the plan -> act -> observe loop is visibly runnable end-to-end. Later slices
// replace these with the CareTriageAgent domain (red-flag guardrail, real symptom KB / clinic
// tools, the mock planner) and a text trace tree.

var message = args.Length > 0
    ? string.Join(' ', args)
    : "sore throat and mild fever since yesterday";

var tools = new ToolRegistry(new ITool[] { new DemoSymptomTool() });
var orchestrator = new AgentOrchestrator(new DemoPlanner(), tools);
var ctx = new WorkContext(conversationId: "cli-session");

Console.WriteLine($"> {message}");
var result = await orchestrator.RunTurnAsync(ctx, message, CancellationToken.None);

foreach (var obs in ctx.Observations)
{
    Console.WriteLine($"  [tool] {obs.ToolName} -> {obs.Result.Output.GetRawText()}");
}

Console.WriteLine($"Agent: {result.Message}");
Console.WriteLine("        ⚠ Educational only — not medical advice.");

// Stand-in planner: call the symptom tool, then finish using what it observed.
internal sealed class DemoPlanner : ILlmClient
{
    private int _step;

    public Task<PlanDecision> PlanNextStepAsync(
        WorkContext ctx, IReadOnlyList<ToolDescriptor> tools, CancellationToken ct)
    {
        _step++;
        if (_step == 1)
        {
            return Task.FromResult<PlanDecision>(new PlanDecision.CallTool("symptom_kb", default));
        }

        var advice = ctx.Observations[^1].Result.Output.GetProperty("advice").GetString();
        return Task.FromResult<PlanDecision>(
            new PlanDecision.Finish($"{advice} See a GP if it persists beyond a few days or worsens."));
    }
}

// Stand-in tool: returns canned self-care advice. The real symptom KB arrives in a later slice.
internal sealed class DemoSymptomTool : ITool
{
    public string Name => "symptom_kb";
    public string Description => "Looks up self-care guidance for described symptoms.";
    public JsonElement InputSchema { get; } =
        JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

    public Task<ToolResult> ExecuteAsync(JsonElement args, WorkContext ctx, CancellationToken ct)
    {
        var output = JsonDocument.Parse("""{"advice":"Looks self-manageable; rest and fluids."}""")
            .RootElement.Clone();
        return Task.FromResult(new ToolResult(Success: true, Output: output));
    }
}
