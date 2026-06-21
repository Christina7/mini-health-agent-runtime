using System.Text.Json;
using System.Text.Json.Serialization;

namespace HealthPlanAgent.Planning;

/// <summary>The user's declared intent. Only <see cref="LoseFat"/> is calorie-/deficit-driven.</summary>
public enum HealthGoal
{
    LoseFat,
    ImproveSleep,
    BoostEnergy
}

/// <summary>Which pillar of the plan a daily task belongs to.</summary>
public enum TaskCategory
{
    Nutrition,
    Movement,
    Sleep,
    Recovery
}

/// <summary>A logged day's intake versus the plan's calorie target.</summary>
public enum DayStatus
{
    Under,
    On,
    Over
}

/// <summary>One item on the daily checklist produced by <c>task_decomposer</c>.</summary>
public sealed record PlanTask(TaskCategory Category, string Description);

/// <summary>
/// One day's logged progress from <c>nutrition_calculator</c> + <c>progress_evaluator</c>. The
/// <see cref="Note"/> is a human annotation shown on the card; it does not drive any re-planning.
/// </summary>
public sealed record ProgressEntry(
    int Day,
    int CaloriesLogged,
    DayStatus Status,
    int TasksCompleted,
    int TasksTotal,
    string Note);

/// <summary>
/// The living artifact of the planning agent: the plan card (targets + timeline + summary), the daily
/// checklist, and the accumulating per-day progress. Carried back from the planner as JSON — the same
/// contract for the mock planner and a future LLM — and re-materialized via <see cref="FromJson"/> by
/// the host/session, which owns it across turns. The disclaimer is always present.
/// </summary>
public sealed record HealthPlanResult
{
    public HealthGoal Goal { get; init; }
    public int DailyCalorieTarget { get; init; }
    public int DailyProteinTargetGrams { get; init; }

    /// <summary>The achievable timeline reported on the card (may differ from the requested TargetDays).</summary>
    public int TimelineDays { get; init; }

    public string Summary { get; init; } = string.Empty;
    public IReadOnlyList<PlanTask> Tasks { get; init; } = Array.Empty<PlanTask>();
    public IReadOnlyList<ProgressEntry> Progress { get; init; } = Array.Empty<ProgressEntry>();

    /// <summary>True when a tool failure forced a safe, conservative skeleton instead of a full plan.</summary>
    public bool Degraded { get; init; }

    public string Disclaimer { get; init; } = "Educational only — not medical advice.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public JsonElement ToJson() => JsonSerializer.SerializeToElement(this, JsonOptions);

    public static HealthPlanResult FromJson(JsonElement element) =>
        element.Deserialize<HealthPlanResult>(JsonOptions)
        ?? throw new JsonException("Could not deserialize HealthPlanResult.");
}
