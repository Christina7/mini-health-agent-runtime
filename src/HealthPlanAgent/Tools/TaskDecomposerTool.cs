using System.Text.Json;
using AgentRuntime.Context;
using AgentRuntime.Tools;
using HealthPlanAgent.Planning;

namespace HealthPlanAgent.Tools;

/// <summary>
/// Turns the plan into a daily checklist across the four pillars (nutrition / movement / sleep /
/// recovery). It consumes <c>plan_generator</c>'s calorie target so the nutrition task is concrete,
/// and tailors the list to the goal. Deterministic and offline.
/// </summary>
public sealed class TaskDecomposerTool : ITool
{
    public string Name => "task_decomposer";
    public string Description => "Breaks the plan into a daily nutrition/movement/sleep/recovery checklist.";

    public JsonElement InputSchema { get; } =
        JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

    public Task<ToolResult> ExecuteAsync(JsonElement args, WorkContext ctx, CancellationToken ct)
    {
        var goal = args.GetProperty("goal").Deserialize<HealthGoal>(PlanJson.Options);

        var plan = ctx.Observations.LastOrDefault(o => o.ToolName == "plan_generator");
        int? calories = plan is { Result.Success: true }
            && plan.Result.Output.TryGetProperty("dailyCalorieTarget", out var c)
            ? c.GetInt32()
            : null;

        var tasks = BuildTasks(goal, calories);
        var output = JsonSerializer.SerializeToElement(new { tasks }, PlanJson.Options);
        return Task.FromResult(new ToolResult(Success: true, Output: output));
    }

    private static List<PlanTask> BuildTasks(HealthGoal goal, int? calories)
    {
        var nutrition = calories is { } kcal
            ? $"Stay near {kcal} kcal today; prioritise protein and whole foods."
            : "Eat balanced, protein-forward meals.";

        var movement = goal switch
        {
            HealthGoal.LoseFat => "30 minutes brisk activity plus a short walk after meals.",
            HealthGoal.BoostEnergy => "20 minutes of moderate movement to lift energy.",
            _ => "A gentle 15-minute walk.",
        };

        return new List<PlanTask>
        {
            new(TaskCategory.Nutrition, nutrition),
            new(TaskCategory.Movement, movement),
            new(TaskCategory.Sleep, "Aim for 7–9 hours; keep a consistent bedtime."),
            new(TaskCategory.Recovery, "Hydrate and take 5 minutes to de-stress."),
        };
    }
}
