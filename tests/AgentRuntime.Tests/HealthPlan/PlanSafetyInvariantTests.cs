using AgentRuntime.Config;
using HealthPlanAgent;
using HealthPlanAgent.Config;
using HealthPlanAgent.Planning;

namespace AgentRuntime.Tests.HealthPlan;

/// <summary>
/// The plan agent's safety invariant, mirroring triage's red-flag guarantee: <see cref="HealthPlanSession"/>
/// registers <c>UnsafeGoalGuardrail</c> unconditionally, so no config/flight can switch it off. The
/// guardrail reads the typed envelope (not the message text) and escalates only on a LoseFat goal that
/// is genuinely unsafe — already underweight, would become underweight, or demands a crash deficit.
/// Safe-but-aggressive goals pass; log turns (no goal/profile) never trip it.
/// </summary>
public class PlanSafetyInvariantTests
{
    private const string BaseJson = """
    {
      "agent": { "maxSteps": 6, "llmProvider": "mock" },
      "tools": {
        "profile_analyzer": { "enabled": true },
        "plan_generator": { "enabled": true },
        "task_decomposer": { "enabled": true },
        "nutrition_calculator": { "enabled": true },
        "progress_evaluator": { "enabled": true }
      },
      "plan": { "deficitCapFraction": 0.20, "proteinGramsPerKg": 1.8, "calorieFloor": 1200 },
      "resilience": { "toolMaxRetries": 2 }
    }
    """;

    private static readonly Dictionary<string, string> Flights = new()
    {
        ["disable-plan-generator"] = """[ { "op": "replace", "path": "/tools/plan_generator/enabled", "value": false } ]""",
    };

    private static HealthPlanSession Session(params string[] flights)
    {
        var config = new RuntimeConfigProvider(BaseJson, Flights).Resolve<HealthPlanConfig>(flights);
        return new HealthPlanSession(config);
    }

    private static PlanEnvelope CreateLoseFat(double weightKg, double goalKg, int targetDays = 84) =>
        new(PlanAction.Create, HealthGoal.LoseFat,
            new HealthProfile(30, Sex.Male, weightKg, 180, ActivityLevel.Moderate, targetDays, goalKg));

    [Theory]
    [InlineData(55, 50, 84)]   // already underweight (BMI ~17)
    [InlineData(70, 55, 84)]   // goal weight is underweight (BMI ~17)
    [InlineData(90, 80, 7)]    // crash: 10 kg in 7 days implies intake far below the floor
    public async Task Unsafe_lose_fat_goal_escalates_and_produces_no_plan(double weightKg, double goalKg, int targetDays)
    {
        var session = Session();

        var turn = await session.SubmitAsync(CreateLoseFat(weightKg, goalKg, targetDays), CancellationToken.None);

        Assert.Null(turn.Result);                                              // no plan payload
        Assert.Contains("consult", turn.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(session.CurrentPlan);
    }

    [Fact]
    public async Task Safe_but_aggressive_goal_is_allowed()
    {
        var session = Session();

        var turn = await session.SubmitAsync(CreateLoseFat(weightKg: 90, goalKg: 80, targetDays: 84), CancellationToken.None);

        Assert.NotNull(turn.Result);
        Assert.NotNull(session.CurrentPlan);
        Assert.False(session.CurrentPlan!.Degraded);
    }

    [Fact]
    public async Task Guardrail_fires_even_when_a_flight_disables_plan_generator()
    {
        // Safety runs before tools, so disabling a tool cannot get past the guardrail.
        var session = Session("disable-plan-generator");

        var turn = await session.SubmitAsync(CreateLoseFat(weightKg: 55, goalKg: 50), CancellationToken.None);

        Assert.Null(turn.Result);
        Assert.Contains("consult", turn.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Companion_the_flight_really_disables_the_tool()
    {
        // A safe goal under the same flight degrades — proving the flight is real, so the guardrail's
        // immunity above isn't just because the flight did nothing.
        var session = Session("disable-plan-generator");

        var turn = await session.SubmitAsync(CreateLoseFat(weightKg: 90, goalKg: 80, targetDays: 84), CancellationToken.None);

        var plan = HealthPlanResult.FromJson(turn.Result!.Value);
        Assert.True(plan.Degraded);
    }

    [Fact]
    public async Task Log_turn_does_not_fire_the_guardrail()
    {
        var session = Session();
        await session.SubmitAsync(CreateLoseFat(weightKg: 90, goalKg: 80, targetDays: 84), CancellationToken.None);

        var turn = await session.SubmitAsync(new PlanEnvelope(PlanAction.Log, Log: new DayLog(2200, 3)), CancellationToken.None);

        var plan = HealthPlanResult.FromJson(turn.Result!.Value);
        Assert.False(plan.Degraded);
        Assert.Single(plan.Progress); // it logged, it didn't escalate
    }
}
