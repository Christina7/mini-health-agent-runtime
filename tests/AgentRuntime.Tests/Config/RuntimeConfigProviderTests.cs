using System.Text.Json.Nodes;
using AgentRuntime.Config;

namespace AgentRuntime.Tests.Config;

public class RuntimeConfigProviderTests
{
    private const string BaseJson = """
    {
      "agent":  { "maxSteps": 6 },
      "tools":  { "clinic_finder": { "enabled": true } },
      "triage": { "selfCareMaxScore": 2 }
    }
    """;

    // Slice 9: with no flights, the effective config is exactly the base config.
    [Fact]
    public void With_no_flights_returns_base_values()
    {
        var config = new RuntimeConfigProvider(BaseJson).Resolve();

        Assert.Equal(6, config["agent"]!["maxSteps"]!.GetValue<int>());
        Assert.True(config["tools"]!["clinic_finder"]!["enabled"]!.GetValue<bool>());
    }

    // A JSON-Patch flight overlay replaces a value; unpatched paths are untouched.
    [Fact]
    public void A_flight_overlay_replaces_a_value()
    {
        var flights = new Dictionary<string, string>
        {
            ["disable-clinic-finder"] = """[ { "op": "replace", "path": "/tools/clinic_finder/enabled", "value": false } ]""",
        };

        var config = new RuntimeConfigProvider(BaseJson, flights).Resolve("disable-clinic-finder");

        Assert.False(config["tools"]!["clinic_finder"]!["enabled"]!.GetValue<bool>());
        Assert.Equal(6, config["agent"]!["maxSteps"]!.GetValue<int>()); // untouched
    }

    // Ordered flights apply in sequence; the last one wins on a shared path.
    [Fact]
    public void Ordered_flights_apply_in_sequence()
    {
        var flights = new Dictionary<string, string>
        {
            ["raise-threshold"] = """[ { "op": "replace", "path": "/triage/selfCareMaxScore", "value": 3 } ]""",
            ["raise-again"] = """[ { "op": "replace", "path": "/triage/selfCareMaxScore", "value": 4 } ]""",
        };

        var config = new RuntimeConfigProvider(BaseJson, flights).Resolve("raise-threshold", "raise-again");

        Assert.Equal(4, config["triage"]!["selfCareMaxScore"]!.GetValue<int>());
    }

    // Flights are allow-listed: a name that isn't a known flight is rejected (can't post arbitrary patches).
    [Fact]
    public void Unknown_flight_is_rejected()
    {
        var provider = new RuntimeConfigProvider(BaseJson);

        Assert.Throws<ArgumentException>(() => provider.Resolve("nonexistent-flight"));
    }
}
