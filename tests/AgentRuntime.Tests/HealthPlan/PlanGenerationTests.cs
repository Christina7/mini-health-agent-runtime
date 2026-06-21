using System.Text.Json;
using System.Text.Json.Serialization;
using AgentRuntime.Context;
using AgentRuntime.Orchestration;
using AgentRuntime.Observability;
using AgentRuntime.Tools;
using HealthPlanAgent.Planning;
using HealthPlanAgent.Tools;

namespace AgentRuntime.Tests.HealthPlan;

/// <summary>
/// The create-turn chain: profile_analyzer -> plan_generator -> task_decomposer -> finish. There is no
/// HealthPlanSession yet (slice 6), so the test plays the session's role — it owns the
/// <see cref="TurnInputHolder"/> and drives the orchestrator directly. Pins: the planner walks all
/// three tools into a populated plan; plan_generator <b>consumes</b> profile_analyzer's TDEE (it fails
/// without that observation rather than recomputing); and profile_analyzer degrades — never throws —
/// on junk input.
/// </summary>
public class PlanGenerationTests
{
    private static readonly JsonSerializerOptions J =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static HealthProfile LoseFat() => new(
        AgeYears: 30, Sex: Sex.Male, WeightKg: 90, HeightCm: 180,
        ActivityLevel: ActivityLevel.Moderate, TargetDays: 84, GoalWeightKg: 80);

    private static AgentOrchestrator CreateChain(TurnInputHolder holder) =>
        new(
            new MockHealthPlanner(holder),
            new ToolRegistry(new ITool[]
            {
                new ProfileAnalyzerTool(),
                new PlanGeneratorTool(PlanPolicy.Default),
                new TaskDecomposerTool(),
            }),
            rootSpanName: "plan.turn");

    private static async Task<TurnResult> RunCreate(HealthProfile profile, HealthGoal goal = HealthGoal.LoseFat)
    {
        var holder = new TurnInputHolder { Current = new PlanEnvelope(PlanAction.Create, goal, profile) };
        return await CreateChain(holder).RunTurnAsync(new WorkContext("c"), "create my plan", CancellationToken.None);
    }

    [Fact]
    public async Task Create_turn_walks_all_three_tools_into_a_populated_plan()
    {
        var turn = await RunCreate(LoseFat());

        var spans = turn.Trace!.Flatten().Select(n => n.Name).ToList();
        Assert.Contains("tool:profile_analyzer", spans);
        Assert.Contains("tool:plan_generator", spans);
        Assert.Contains("tool:task_decomposer", spans);

        var plan = HealthPlanResult.FromJson(turn.Result!.Value);
        Assert.False(plan.Degraded);
        Assert.Equal(HealthGoal.LoseFat, plan.Goal);
        Assert.NotEmpty(plan.Tasks);
        Assert.True(plan.TimelineDays > 0);

        // A real deficit: below maintenance (TDEE), but never below the policy's calorie floor.
        var tdee = PlanMath.Tdee(LoseFat());
        Assert.InRange(plan.DailyCalorieTarget, PlanPolicy.Default.CalorieFloor, (int)tdee);
        Assert.True(plan.DailyProteinTargetGrams > 0);
    }

    [Fact]
    public async Task Plan_generator_consumes_the_analyzer_tdee_and_cannot_proceed_without_it()
    {
        // Run plan_generator with no profile_analyzer observation in context: it must degrade
        // (Success:false), proving it reads TDEE from the upstream tool rather than recomputing it.
        var planGenerator = new PlanGeneratorTool(PlanPolicy.Default);
        var args = JsonSerializer.SerializeToElement(
            new { goal = HealthGoal.LoseFat, currentKg = 90.0, goalKg = 80.0, targetDays = 84 }, J);

        var result = await planGenerator.ExecuteAsync(args, new WorkContext("c"), CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Profile_analyzer_rounds_deterministically()
    {
        var tool = new ProfileAnalyzerTool();
        var args = JsonSerializer.SerializeToElement(LoseFat(), J);

        var result = await tool.ExecuteAsync(args, new WorkContext("c"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Output.GetProperty("bmr").TryGetInt32(out _));   // whole calories
        Assert.True(result.Output.GetProperty("tdee").TryGetInt32(out _));  // whole calories
        var bmi = result.Output.GetProperty("bmi").GetDouble();
        Assert.Equal(Math.Round(bmi, 1), bmi);                              // 1 decimal place
    }

    [Fact]
    public async Task Profile_analyzer_degrades_not_throws_on_junk_input()
    {
        // HeightCm = 0 would divide-by-zero a naive BMI; the tool must return Success:false, not throw.
        var tool = new ProfileAnalyzerTool();
        var junk = LoseFat() with { HeightCm = 0 };
        var args = JsonSerializer.SerializeToElement(junk, J);

        var result = await tool.ExecuteAsync(args, new WorkContext("c"), CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Junk_profile_yields_a_safe_degraded_plan_not_a_crash()
    {
        var turn = await RunCreate(LoseFat() with { HeightCm = 0 });

        var plan = HealthPlanResult.FromJson(turn.Result!.Value);
        Assert.True(plan.Degraded);

        // The chain short-circuits at the failed analysis — plan_generator is never reached.
        var spans = turn.Trace!.Flatten().Select(n => n.Name).ToList();
        Assert.DoesNotContain("tool:plan_generator", spans);
    }
}
