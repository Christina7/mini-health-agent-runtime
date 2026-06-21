using System.Text.Json;
using AgentRuntime.Context;
using AgentRuntime.Tools;
using HealthPlanAgent.Planning;

namespace HealthPlanAgent.Tools;

/// <summary>
/// Compares a day's logged calories against the plan's target → under / on / over, with the remaining
/// allowance. A small tolerance band counts "close enough" as on-target. Deterministic and offline.
/// </summary>
public sealed class NutritionCalculatorTool : ITool
{
    private const int ToleranceKcal = 100;

    public string Name => "nutrition_calculator";
    public string Description => "Compares a day's logged calories against the plan target.";

    public JsonElement InputSchema { get; } =
        JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

    public Task<ToolResult> ExecuteAsync(JsonElement args, WorkContext ctx, CancellationToken ct)
    {
        var target = args.GetProperty("calorieTarget").GetInt32();
        var logged = args.GetProperty("caloriesLogged").GetInt32();
        var remaining = target - logged;

        var status = remaining > ToleranceKcal ? DayStatus.Under
            : remaining < -ToleranceKcal ? DayStatus.Over
            : DayStatus.On;

        var output = JsonSerializer.SerializeToElement(
            new { status, remaining, caloriesLogged = logged, calorieTarget = target }, PlanJson.Options);
        return Task.FromResult(new ToolResult(Success: true, Output: output));
    }
}
