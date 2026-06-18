using AgentRuntime.Context;
using AgentRuntime.Orchestration;
using AgentRuntime.Tools;
using CareTriageAgent.Guardrails;
using CareTriageAgent.Tools;
using CareTriageAgent.Triage;

// CLI host: drives the real CareTriageAgent brain through the runtime orchestrator on deterministic,
// offline data. The red-flag guardrail runs first; otherwise the MockTriagePlanner scores symptoms
// via the KB tool and classifies urgency with TriagePolicy. (Data is in-memory here; loading from
// Data/*.json + RuntimeConfig arrives in a later slice. A live trace tree arrives with the web host.)

var message = args.Length > 0
    ? string.Join(' ', args)
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

var tools = new ToolRegistry(new ITool[] { new SymptomKnowledgeBaseTool(knowledgeBase) });

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
var orchestrator = new AgentOrchestrator(new MockTriagePlanner(policy), tools, guardrails);
var ctx = new WorkContext(conversationId: "cli-session");

Console.WriteLine($"> {message}");
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
    Console.WriteLine($"  │ {triage.Disclaimer}");
    Console.WriteLine("  └──────────────────────────────────────");
}
else
{
    Console.WriteLine("        ⚠ Educational only — not medical advice.");
}
