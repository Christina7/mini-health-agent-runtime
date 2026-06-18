using System.Text.Json;
using AgentRuntime.Context;
using AgentRuntime.Failure;
using AgentRuntime.Orchestration;
using AgentRuntime.Tools;
using CareTriageAgent.Guardrails;
using CareTriageAgent.Tools;
using CareTriageAgent.Triage;

// CLI host: drives the real CareTriageAgent brain through the runtime orchestrator on deterministic,
// offline data. The red-flag guardrail runs first; otherwise the MockTriagePlanner scores symptoms
// via the KB tool and classifies urgency with TriagePolicy. Tool calls run through an ExecutionScope,
// so --break-symptom-kb makes the tool fail and the turn degrades to a safe answer instead of crashing.
//
// Usage: dotnet run --project src/CareTriageAgent.Cli -- [--break-symptom-kb] <symptom text>

var flags = args.Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToHashSet();
var messageParts = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
var breakSymptomKb = flags.Contains("--break-symptom-kb");
var message = messageParts.Length > 0
    ? string.Join(' ', messageParts)
    : "sore throat and mild fever since yesterday";

var knowledgeBase = new[]
{
    new SymptomEntry("sore_throat", new[] { "sore throat", "throat pain" }, BaseSeverity: 1, SelfCareAdvice: "Rest, fluids, and throat lozenges usually help."),
    new SymptomEntry("fever", new[] { "fever", "high temperature" }, BaseSeverity: 1, SelfCareAdvice: "Stay hydrated and monitor your temperature."),
    new SymptomEntry("headache", new[] { "headache" }, BaseSeverity: 1, SelfCareAdvice: "Rest and over-the-counter pain relief may help."),
    new SymptomEntry("cough", new[] { "cough" }, BaseSeverity: 1, SelfCareAdvice: "A cough often clears on its own within a couple of weeks."),
    new SymptomEntry("dizziness", new[] { "dizzy", "dizziness", "lightheaded" }, BaseSeverity: 3, SelfCareAdvice: "Sit or lie down; avoid sudden movements."),
    new SymptomEntry("abdominal_pain", new[] { "abdominal pain", "stomach pain", "belly pain" }, BaseSeverity: 4, SelfCareAdvice: "Note where the pain is and whether it worsens."),
    new SymptomEntry("chest_pain", new[] { "chest pain", "chest tightness" }, BaseSeverity: 7, SelfCareAdvice: "Chest pain should be assessed promptly."),
    new SymptomEntry("breathing_difficulty", new[] { "shortness of breath", "trouble breathing", "can't breathe" }, BaseSeverity: 8, SelfCareAdvice: "Difficulty breathing needs prompt assessment."),
};

ITool symptomTool = breakSymptomKb
    ? new BrokenSymptomTool()
    : new SymptomKnowledgeBaseTool(knowledgeBase);

var tools = new ToolRegistry(new[] { symptomTool });

var guardrails = new IGuardrail[]
{
    new RedFlagGuardrail(new[]
    {
        new RedFlagRule(
            Id: "cardiac",
            AllOf: new[] { "chest pain", "shortness of breath" },
            Message: "🚨 Possible cardiac emergency — call your local emergency number / go to the ER now."),
    }),
};

var policy = new TriagePolicy(new TriageThresholds(SelfCareMaxScore: 2, SeeGpMaxScore: 5, UrgentCareMaxScore: 8));
var scope = new ExecutionScope(maxRetries: 2);
var orchestrator = new AgentOrchestrator(new MockTriagePlanner(policy), tools, guardrails, scope);
var ctx = new WorkContext(conversationId: "cli-session");

Console.WriteLine($"> {message}{(breakSymptomKb ? "   [--break-symptom-kb]" : "")}");
var turn = await orchestrator.RunTurnAsync(ctx, message, CancellationToken.None);

Console.WriteLine();
Console.WriteLine($"Agent: {turn.Message}");

if (turn.Result is { } resultJson)
{
    var triage = TriageResult.FromJson(resultJson);
    Console.WriteLine();
    Console.WriteLine("  ┌─ Triage ─────────────────────────────");
    Console.WriteLine($"  │ Urgency:     {triage.Urgency}");
    Console.WriteLine($"  │ Action:      {triage.RecommendedAction}");
    Console.WriteLine($"  │ Tools used:  {string.Join(", ", triage.ToolsInvoked)}");
    if (triage.Degraded)
    {
        Console.WriteLine("  │ Status:      ⚠ DEGRADED (a tool failed; fell back to a safe answer)");
    }
    Console.WriteLine($"  │ {triage.Disclaimer}");
    Console.WriteLine("  └──────────────────────────────────────");
}
else
{
    Console.WriteLine("        ⚠ Educational only — not medical advice.");
}

// A tool that always fails, simulating a missing data file. Used by --break-symptom-kb to show the
// runtime's retry -> degrade -> safe-fallback behavior live.
internal sealed class BrokenSymptomTool : ITool
{
    public string Name => "symptom_kb";
    public string Description => "Symptom KB (simulated failure).";
    public JsonElement InputSchema { get; } = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

    public Task<ToolResult> ExecuteAsync(JsonElement args, WorkContext ctx, CancellationToken ct) =>
        throw new InvalidOperationException("symptom KB data file is missing");
}
