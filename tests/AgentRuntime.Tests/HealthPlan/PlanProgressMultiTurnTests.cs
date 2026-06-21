using AgentRuntime.Context;
using AgentRuntime.Orchestration;
using AgentRuntime.Tests.Support;
using AgentRuntime.Tools;
using HealthPlanAgent.Planning;
using HealthPlanAgent.Tools;

namespace AgentRuntime.Tests.HealthPlan;

/// <summary>
/// The log-progress path and the living artifact across turns. There is no HealthPlanSession yet
/// (slice 6), so the test plays its role: it owns the <see cref="TurnInputHolder"/> and threads the
/// prior <see cref="HealthPlanResult"/> back into the next turn via <c>PriorArtifact</c>. Pins:
/// create → log appends a <see cref="ProgressEntry"/>, successive logs accumulate, the log turn walks
/// the nutrition → progress chain, and a degraded log turn preserves the plan instead of corrupting it.
/// </summary>
public class PlanProgressMultiTurnTests
{
    private static HealthProfile Profile() => new(
        AgeYears: 30, Sex: Sex.Male, WeightKg: 90, HeightCm: 180,
        ActivityLevel: ActivityLevel.Moderate, TargetDays: 84, GoalWeightKg: 80);

    private sealed class Harness
    {
        private readonly AgentOrchestrator _orchestrator;
        private readonly WorkContext _ctx = new("c");
        private readonly TurnInputHolder _holder = new();

        public Harness(bool breakNutrition = false)
        {
            ITool nutrition = breakNutrition ? new ThrowingTool("nutrition_calculator") : new NutritionCalculatorTool();
            var tools = new ITool[]
            {
                new ProfileAnalyzerTool(), new PlanGeneratorTool(PlanPolicy.Default), new TaskDecomposerTool(),
                nutrition, new ProgressEvaluatorTool(),
            };
            _orchestrator = new AgentOrchestrator(
                new MockHealthPlanner(_holder), new ToolRegistry(tools),
                scope: new AgentRuntime.Failure.ExecutionScope(maxRetries: 0), rootSpanName: "plan.turn");
        }

        public async Task<HealthPlanResult> Create()
        {
            _holder.PriorArtifact = null;
            _holder.Current = new PlanEnvelope(PlanAction.Create, HealthGoal.LoseFat, Profile());
            var turn = await _orchestrator.RunTurnAsync(_ctx, "create my plan", CancellationToken.None);
            return HealthPlanResult.FromJson(turn.Result!.Value);
        }

        public async Task<(TurnResult Turn, HealthPlanResult Plan)> Log(HealthPlanResult prior, int calories, int tasksDone)
        {
            _holder.PriorArtifact = prior;
            _holder.Current = new PlanEnvelope(PlanAction.Log, Log: new DayLog(calories, tasksDone));
            var turn = await _orchestrator.RunTurnAsync(_ctx, "log my day", CancellationToken.None);
            return (turn, HealthPlanResult.FromJson(turn.Result!.Value));
        }
    }

    [Fact]
    public async Task Log_turn_appends_a_progress_entry_and_preserves_the_card()
    {
        var h = new Harness();
        var plan = await h.Create();

        var (_, updated) = await h.Log(plan, calories: 2200, tasksDone: 3); // target 2331 → 131 under

        var entry = Assert.Single(updated.Progress);
        Assert.Equal(1, entry.Day);
        Assert.Equal(2200, entry.CaloriesLogged);
        Assert.Equal(DayStatus.Under, entry.Status);
        Assert.Equal(3, entry.TasksCompleted);
        Assert.Equal(plan.Tasks.Count, entry.TasksTotal);

        // The plan card itself is unchanged — only progress grew.
        Assert.Equal(plan.DailyCalorieTarget, updated.DailyCalorieTarget);
        Assert.Equal(plan.Tasks, updated.Tasks);
        Assert.False(updated.Degraded);
    }

    [Fact]
    public async Task Successive_logs_accumulate_progress()
    {
        var h = new Harness();
        var plan = await h.Create();

        var (_, afterDay1) = await h.Log(plan, calories: 2200, tasksDone: 3);       // Under
        var (_, afterDay2) = await h.Log(afterDay1, calories: 2500, tasksDone: 4);  // 169 over → Over

        Assert.Equal(2, afterDay2.Progress.Count);
        Assert.Equal(new[] { 1, 2 }, afterDay2.Progress.Select(p => p.Day));
        Assert.Equal(DayStatus.Over, afterDay2.Progress[1].Status);
    }

    [Fact]
    public async Task Log_turn_walks_the_nutrition_then_progress_chain()
    {
        var h = new Harness();
        var plan = await h.Create();

        var (turn, _) = await h.Log(plan, calories: 2300, tasksDone: 4);

        var spans = turn.Trace!.Flatten().Select(n => n.Name).ToList();
        Assert.Contains("tool:nutrition_calculator", spans);
        Assert.Contains("tool:progress_evaluator", spans);
        Assert.DoesNotContain("tool:plan_generator", spans); // it's a log turn, not a create turn
    }

    [Fact]
    public async Task Degraded_log_turn_preserves_the_plan_and_does_not_leak_forward()
    {
        var h = new Harness(breakNutrition: true);
        var plan = await h.Create(); // create still works; only nutrition_calculator is broken

        var (turn, updated) = await h.Log(plan, calories: 2200, tasksDone: 3);

        Assert.True(turn.Degraded);
        Assert.True(updated.Degraded);
        Assert.Empty(updated.Progress);                              // no bogus entry appended
        Assert.Equal(plan.DailyCalorieTarget, updated.DailyCalorieTarget); // the plan itself survives
    }
}
