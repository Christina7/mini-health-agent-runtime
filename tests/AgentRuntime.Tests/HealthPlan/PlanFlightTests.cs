using AgentRuntime.Config;
using HealthPlanAgent.Config;
using HealthPlanAgent.Planning;

namespace AgentRuntime.Tests.HealthPlan;

/// <summary>
/// Config-driven plan generation, the plan-side mirror of <c>ConfigDrivenTriageTests</c>: the same
/// goal + profile yields a different calorie target under the aggressive vs conservative flight, with
/// no recompile, because the flight moves the deficit <b>cap</b> in <see cref="PlanPolicy"/>.
/// </summary>
public class PlanFlightTests
{
    private const string BaseJson = """
    {
      "plan": { "deficitCapFraction": 0.20, "proteinGramsPerKg": 1.8, "calorieFloor": 1200 }
    }
    """;

    private static readonly Dictionary<string, string> Flights = new()
    {
        ["aggressive-plan"] = """[ { "op": "replace", "path": "/plan/deficitCapFraction", "value": 0.25 } ]""",
        ["conservative-plan"] = """[ { "op": "replace", "path": "/plan/deficitCapFraction", "value": 0.15 } ]""",
    };

    // TDEE 2914 with a 10 kg / 84-day goal: the requested deficit exceeds every cap, so the cap binds
    // in all three cases and the calorie target differs purely by flight.
    private static int CalorieTargetUnder(params string[] flights)
    {
        var config = new RuntimeConfigProvider(BaseJson, Flights).Resolve<HealthPlanConfig>(flights);
        var policy = new PlanPolicy(config.Plan.DeficitCapFraction, config.Plan.ProteinGramsPerKg, config.Plan.CalorieFloor);
        return policy.Prescribe(HealthGoal.LoseFat, tdee: 2914, currentKg: 90, goalKg: 80, targetDays: 84).DailyCalorieTarget;
    }

    [Fact]
    public void Flights_reprice_the_same_goal_without_a_recompile()
    {
        var aggressive = CalorieTargetUnder("aggressive-plan");
        var baseline = CalorieTargetUnder();
        var conservative = CalorieTargetUnder("conservative-plan");

        // Aggressive = a bigger allowed deficit = fewer calories; conservative = the reverse.
        Assert.True(aggressive < baseline, $"aggressive {aggressive} should be < base {baseline}");
        Assert.True(baseline < conservative, $"base {baseline} should be < conservative {conservative}");
    }
}
