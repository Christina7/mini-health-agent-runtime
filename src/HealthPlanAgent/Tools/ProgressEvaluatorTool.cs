using System.Text.Json;
using AgentRuntime.Context;
using AgentRuntime.Tools;
using HealthPlanAgent.Planning;

namespace HealthPlanAgent.Tools;

/// <summary>
/// Consumes <c>nutrition_calculator</c>'s result plus the day's task completion and produces a
/// <see cref="ProgressEntry"/> with a short human note. The note is shown on the card; it does not
/// drive any re-planning (that feedback loop is out of scope). Degrades if the nutrition result is
/// missing rather than inventing one.
/// </summary>
public sealed class ProgressEvaluatorTool : ITool
{
    public string Name => "progress_evaluator";
    public string Description => "Turns a day's nutrition result and task completion into a progress entry.";

    public JsonElement InputSchema { get; } =
        JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

    public Task<ToolResult> ExecuteAsync(JsonElement args, WorkContext ctx, CancellationToken ct)
    {
        var nutrition = ctx.Observations.LastOrDefault(o => o.ToolName == "nutrition_calculator");
        if (nutrition is not { Result.Success: true } || nutrition.Result.Output.ValueKind != JsonValueKind.Object)
        {
            return Task.FromResult(new ToolResult(Success: false, Output: default, Error: "no nutrition result to evaluate"));
        }

        var status = nutrition.Result.Output.GetProperty("status").Deserialize<DayStatus>(PlanJson.Options);
        var caloriesLogged = nutrition.Result.Output.GetProperty("caloriesLogged").GetInt32();

        var day = args.GetProperty("day").GetInt32();
        var tasksCompleted = args.GetProperty("tasksCompleted").GetInt32();
        var tasksTotal = args.GetProperty("tasksTotal").GetInt32();

        var entry = new ProgressEntry(day, caloriesLogged, status, tasksCompleted, tasksTotal, Note(status, tasksCompleted, tasksTotal));
        var output = JsonSerializer.SerializeToElement(entry, PlanJson.Options);
        return Task.FromResult(new ToolResult(Success: true, Output: output));
    }

    private static string Note(DayStatus status, int done, int total)
    {
        var calorie = status switch
        {
            DayStatus.Over => "Over your calorie target — ease back tomorrow.",
            DayStatus.Under => "Under target — good, just don't undereat.",
            _ => "On target.",
        };
        var tasks = done >= total ? "All tasks done!" : $"{done}/{total} tasks done.";
        return $"{calorie} {tasks}";
    }
}
