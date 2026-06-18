using System.Text.Json;
using AgentRuntime.Config;
using AgentRuntime.Context;
using AgentRuntime.Failure;
using AgentRuntime.Orchestration;
using AgentRuntime.Tools;
using CareTriageAgent.Config;
using CareTriageAgent.Guardrails;
using CareTriageAgent.Tools;
using CareTriageAgent.Triage;

// CLI host: drives the real CareTriageAgent brain through the runtime orchestrator on deterministic,
// offline data. Behavior is config-driven: a base runtimeconfig.json plus allow-listed JSON-Patch
// flights (config/flights/*.json) decide thresholds, retries, and which tools are enabled — all
// changeable with --flight and no recompile. The red-flag guardrail always runs first.
//
// Usage: dotnet run --project src/CareTriageAgent.Cli -- [--flight <name>]... [--break-symptom-kb] <symptom text>

var activeFlights = new List<string>();
var messageParts = new List<string>();
var breakSymptomKb = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--flight" when i + 1 < args.Length:
            activeFlights.Add(args[++i]);
            break;
        case "--break-symptom-kb":
            breakSymptomKb = true;
            break;
        default:
            messageParts.Add(args[i]);
            break;
    }
}

var message = messageParts.Count > 0
    ? string.Join(' ', messageParts)
    : "sore throat and mild fever since yesterday";

// Load base config + allow-listed flights from disk, then resolve the effective config.
var configDir = Path.Combine(AppContext.BaseDirectory, "config");
var baseJson = File.ReadAllText(Path.Combine(configDir, "runtimeconfig.json"));
var flightsDir = Path.Combine(configDir, "flights");
var flights = Directory.Exists(flightsDir)
    ? Directory.GetFiles(flightsDir, "*.json").ToDictionary(Path.GetFileNameWithoutExtension, File.ReadAllText)
    : new Dictionary<string, string>();

CareTriageConfig config;
try
{
    config = new RuntimeConfigProvider(baseJson, flights).Resolve<CareTriageConfig>(activeFlights.ToArray());
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"Config error: {ex.Message}");
    Console.Error.WriteLine($"Available flights: {string.Join(", ", flights.Keys.OrderBy(k => k))}");
    return 1;
}

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

// Tools are registered only if the effective config enables them (a flight can switch them off).
var toolList = new List<ITool>();
if (config.IsToolEnabled("symptom_kb"))
{
    toolList.Add(breakSymptomKb ? new BrokenSymptomTool() : new SymptomKnowledgeBaseTool(knowledgeBase));
}

var tools = new ToolRegistry(toolList);

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

var policy = new TriagePolicy(new TriageThresholds(
    config.Triage.SelfCareMaxScore, config.Triage.SeeGpMaxScore, config.Triage.UrgentCareMaxScore));
var scope = new ExecutionScope(config.Resilience.ToolMaxRetries);
var orchestrator = new AgentOrchestrator(new MockTriagePlanner(policy), tools, guardrails, scope);
var ctx = new WorkContext(conversationId: "cli-session");

var banner = activeFlights.Count > 0 ? $"   [flights: {string.Join(", ", activeFlights)}]" : "";
banner += breakSymptomKb ? "   [--break-symptom-kb]" : "";
Console.WriteLine($"> {message}{banner}");

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
    Console.WriteLine($"  │ Tools used:  {(triage.ToolsInvoked.Count > 0 ? string.Join(", ", triage.ToolsInvoked) : "(none)")}");
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

return 0;

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
