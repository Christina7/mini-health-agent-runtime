using AgentRuntime.Config;
using AgentRuntime.Tools;
using CareTriageAgent;
using CareTriageAgent.Config;
using CareTriageAgent.Guardrails;
using CareTriageAgent.Tools;
using CareTriageAgent.Triage;

namespace AgentRuntime.Tests.CareTriage;

public class SafetyInvariantTests
{
    private const string BaseJson = """
    {
      "agent":      { "maxSteps": 6, "llmProvider": "mock" },
      "tools":      { "symptom_extractor": { "enabled": true }, "symptom_kb": { "enabled": true } },
      "triage":     { "selfCareMaxScore": 2, "seeGpMaxScore": 5, "urgentCareMaxScore": 8 },
      "resilience": { "toolMaxRetries": 2 }
    }
    """;

    private static readonly Dictionary<string, string> Flights = new()
    {
        ["disable-symptom-kb"] = """[ { "op": "replace", "path": "/tools/symptom_kb/enabled", "value": false } ]""",
    };

    private static readonly SymptomEntry[] Kb = { new("sore_throat", new[] { "sore throat" }, BaseSeverity: 1, SelfCareAdvice: "Rest.") };

    private static readonly RedFlagRule[] CardiacRules =
    {
        new("cardiac", new[] { "chest pain", "shortness of breath" }, "Possible cardiac emergency — call your local emergency number."),
    };

    private static CareTriageSession SessionWith(params string[] flights)
    {
        var config = new RuntimeConfigProvider(BaseJson, Flights).Resolve<CareTriageConfig>(flights);
        var tools = new ITool[]
        {
            new SymptomExtractorTool(new KeywordSymptomExtractor(Kb)),
            new SymptomKnowledgeBaseTool(Kb),
        };
        return new CareTriageSession(config, tools, CardiacRules);
    }

    // Slice 11 (safety invariant): the red-flag guardrail is not config-driven. Even with the
    // symptom tool disabled by a flight, a cardiac input still escalates to an emergency.
    [Fact]
    public async Task No_flight_can_disable_the_red_flag_guardrail()
    {
        var session = SessionWith("disable-symptom-kb");

        var turn = await session.OnUserMessageAsync("severe chest pain and shortness of breath", CancellationToken.None);

        Assert.Contains("emergency", turn.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Companion: the same flight DOES disable a normal tool — proving the flight is real and the
    // guardrail's immunity above isn't just because the flight did nothing. With the scorer disabled
    // the turn cannot be assessed, so it degrades safely to SeeGp rather than fabricating a score-0
    // SelfCare (the #36 unsafe direction).
    [Fact]
    public async Task Disabling_the_scorer_degrades_safely_to_see_gp()
    {
        var session = SessionWith("disable-symptom-kb");

        var turn = await session.OnUserMessageAsync("sore throat", CancellationToken.None);

        var triage = TriageResult.FromJson(turn.Result!.Value);
        Assert.Empty(triage.ToolsInvoked); // symptom_kb was switched off by the flight
        Assert.Equal(UrgencyLevel.SeeGp, triage.Urgency);
        Assert.True(triage.Degraded);
    }
}
