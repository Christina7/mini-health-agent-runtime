using System.Text.Json;
using AgentRuntime.Config;
using AgentRuntime.Context;
using AgentRuntime.Observability;
using AgentRuntime.Tools;
using CareTriageAgent;
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
    ? Directory.GetFiles(flightsDir, "*.json").ToDictionary(f => Path.GetFileNameWithoutExtension(f)!, File.ReadAllText)
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

// Candidate tools and red-flag rules are domain data, shared with the web host via CareTriageDomain.
// CareTriageSession filters tools by config and registers the guardrail unconditionally (the safety
// invariant), wiring everything from config.
var knowledgeBase = CareTriageDomain.DefaultKnowledgeBase();
var candidateTools = new ITool[]
{
    new SymptomExtractorTool(new KeywordSymptomExtractor(knowledgeBase)),
    breakSymptomKb ? new BrokenSymptomTool() : new SymptomKnowledgeBaseTool(knowledgeBase),
};

var redFlagRules = CareTriageDomain.DefaultRedFlagRules();

var session = new CareTriageSession(config, candidateTools, redFlagRules, conversationId: "cli-session");

var banner = activeFlights.Count > 0 ? $"   [flights: {string.Join(", ", activeFlights)}]" : "";
banner += breakSymptomKb ? "   [--break-symptom-kb]" : "";
Console.WriteLine($"> {message}{banner}");

var turn = await session.OnUserMessageAsync(message, CancellationToken.None);

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

if (turn.Trace is { } trace)
{
    Console.WriteLine();
    Console.WriteLine("[trace]");
    PrintTrace(trace, 0);
}

return 0;

static void PrintTrace(TraceNode node, int depth)
{
    var pad = new string(' ', depth * 2);
    var branch = depth == 0 ? "" : "└ ";
    var degraded = node.Degraded ? "  ⚠ degraded" : "";
    Console.WriteLine($"  {pad}{branch}{node.Name}  ({node.DurationMs:F2} ms){degraded}");
    foreach (var child in node.Children)
    {
        PrintTrace(child, depth + 1);
    }
}
