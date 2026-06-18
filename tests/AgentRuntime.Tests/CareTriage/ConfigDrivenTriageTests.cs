using AgentRuntime.Config;
using CareTriageAgent.Config;
using CareTriageAgent.Triage;

namespace AgentRuntime.Tests.CareTriage;

public class ConfigDrivenTriageTests
{
    private const string BaseJson = """
    {
      "agent":      { "maxSteps": 6, "llmProvider": "mock" },
      "tools":      { "symptom_kb": { "enabled": true } },
      "triage":     { "selfCareMaxScore": 2, "seeGpMaxScore": 5, "urgentCareMaxScore": 8 },
      "resilience": { "toolMaxRetries": 2 }
    }
    """;

    private static readonly Dictionary<string, string> Flights = new()
    {
        ["strict-thresholds"] = """[ { "op": "replace", "path": "/triage/selfCareMaxScore", "value": 0 } ]""",
    };

    private static TriagePolicy PolicyFrom(CareTriageConfig config) =>
        new(new TriageThresholds(config.Triage.SelfCareMaxScore, config.Triage.SeeGpMaxScore, config.Triage.UrgentCareMaxScore));

    // Slice 10: the effective config deserializes into the typed CareTriageConfig and drives policy.
    [Fact]
    public void Base_config_classifies_score_2_as_self_care()
    {
        var config = new RuntimeConfigProvider(BaseJson, Flights).Resolve<CareTriageConfig>();

        Assert.Equal(UrgencyLevel.SelfCare, PolicyFrom(config).Classify(2));
    }

    // The same score lands in a different band once a flight tightens the threshold — no recompile.
    [Fact]
    public void Strict_thresholds_flight_reclassifies_the_same_score()
    {
        var config = new RuntimeConfigProvider(BaseJson, Flights).Resolve<CareTriageConfig>("strict-thresholds");

        Assert.Equal(UrgencyLevel.SeeGp, PolicyFrom(config).Classify(2)); // selfCareMax now 0, so score 2 -> SeeGp
    }
}
