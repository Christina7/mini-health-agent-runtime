using HealthPlanAgent.Planning;

namespace AgentRuntime.Tests.HealthPlan;

/// <summary>
/// Pins the cross-turn artifact contract: <see cref="HealthPlanResult"/> is carried back from the
/// planner as JSON (so the same shape works for the mock and a future LLM) and re-materialized by the
/// host/session each turn. The accumulating progress list must survive the round-trip (the artifact
/// lives in the session and is fed back turn over turn), enums must serialize as strings to match the
/// HTTP contract, and the educational disclaimer is always present.
/// </summary>
public class HealthPlanResultTests
{
    private static HealthPlanResult Sample() => new()
    {
        Goal = HealthGoal.LoseFat,
        DailyCalorieTarget = 1850,
        DailyProteinTargetGrams = 140,
        TimelineDays = 84,
        Summary = "Lose fat at a safe pace.",
        Tasks = new[]
        {
            new PlanTask(TaskCategory.Nutrition, "Hit your calorie target."),
            new PlanTask(TaskCategory.Movement, "30 minutes brisk walking."),
        },
        Progress = new[]
        {
            new ProgressEntry(Day: 1, CaloriesLogged: 1820, Status: DayStatus.Under, TasksCompleted: 4, TasksTotal: 5, Note: "On track."),
            new ProgressEntry(Day: 2, CaloriesLogged: 2100, Status: DayStatus.Over, TasksCompleted: 3, TasksTotal: 5, Note: "150 kcal over — trim tomorrow."),
        },
    };

    [Fact]
    public void Round_trips_through_json_preserving_every_field()
    {
        var original = Sample();

        var restored = HealthPlanResult.FromJson(original.ToJson());

        Assert.Equal(original.Goal, restored.Goal);
        Assert.Equal(original.DailyCalorieTarget, restored.DailyCalorieTarget);
        Assert.Equal(original.DailyProteinTargetGrams, restored.DailyProteinTargetGrams);
        Assert.Equal(original.TimelineDays, restored.TimelineDays);
        Assert.Equal(original.Summary, restored.Summary);
        Assert.Equal(original.Tasks, restored.Tasks);         // records → structural equality
        Assert.Equal(original.Disclaimer, restored.Disclaimer);
    }

    [Fact]
    public void Preserves_a_multi_entry_progress_list()
    {
        // The artifact accumulates a ProgressEntry per logged day; the whole list must survive so the
        // session can feed the prior plan forward into the next turn (not just the latest entry).
        var restored = HealthPlanResult.FromJson(Sample().ToJson());

        Assert.Equal(2, restored.Progress.Count);
        Assert.Equal(Sample().Progress, restored.Progress);
        Assert.Equal(DayStatus.Over, restored.Progress[1].Status);
        Assert.Equal(2100, restored.Progress[1].CaloriesLogged);
    }

    [Fact]
    public void Serializes_enums_as_strings_not_numbers()
    {
        var json = Sample().ToJson();

        Assert.Equal("LoseFat", json.GetProperty("goal").GetString());
        Assert.Equal("Nutrition", json.GetProperty("tasks")[0].GetProperty("category").GetString());
        Assert.Equal("Over", json.GetProperty("progress")[1].GetProperty("status").GetString());
    }

    [Fact]
    public void Always_carries_the_educational_disclaimer()
    {
        // Default (e.g. a degraded skeleton) still carries it, and it survives the round-trip.
        Assert.Contains("not medical advice", new HealthPlanResult().Disclaimer, StringComparison.OrdinalIgnoreCase);

        var restored = HealthPlanResult.FromJson(Sample().ToJson());
        Assert.Contains("not medical advice", restored.Disclaimer, StringComparison.OrdinalIgnoreCase);
    }
}
