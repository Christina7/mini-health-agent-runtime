using System.Text.Json;
using AgentRuntime.Context;
using AgentRuntime.Llm;
using AgentRuntime.Tools;

namespace HealthPlanAgent.Planning;

/// <summary>
/// The deterministic, offline planner for the plan agent (the default <see cref="ILlmClient"/>).
/// State-dependent like the triage planner, but it reads its goal/profile from the session-scoped
/// <see cref="TurnInputHolder"/> (never from the user message, which stays human-readable) and walks
/// the next un-run tool in the create chain: profile_analyzer → plan_generator → task_decomposer →
/// finish. A failed/disabled tool yields a safe, clearly-degraded plan rather than a crash.
/// </summary>
public sealed class MockHealthPlanner : ILlmClient
{
    private const string ProfileAnalyzer = "profile_analyzer";
    private const string PlanGenerator = "plan_generator";
    private const string TaskDecomposer = "task_decomposer";

    private readonly TurnInputHolder _holder;

    public MockHealthPlanner(TurnInputHolder holder) => _holder = holder;

    public Task<PlanDecision> PlanNextStepAsync(WorkContext ctx, IReadOnlyList<ToolDescriptor> tools, CancellationToken ct)
    {
        var envelope = _holder.Current;
        var goal = envelope?.Goal ?? HealthGoal.LoseFat;
        var profile = envelope?.Profile;

        // 1. Analyse the profile.
        var analysis = LastObservation(ctx, ProfileAnalyzer);
        if (analysis is null && profile is not null && Available(tools, ProfileAnalyzer))
        {
            return Call(ProfileAnalyzer, JsonSerializer.SerializeToElement(profile, PlanJson.Options));
        }

        // No profile, or the analysis failed/degraded → conservative skeleton, not a crash.
        if (profile is null || ctx.Degraded || analysis is { Result.Success: false })
        {
            return FinishWith(DegradedSkeleton(goal));
        }

        // 2. Generate the plan from the analysis.
        var plan = LastObservation(ctx, PlanGenerator);
        if (plan is null && Available(tools, PlanGenerator))
        {
            return Call(PlanGenerator, PlanArgs(goal, profile));
        }

        if (plan is null or { Result.Success: false })
        {
            return FinishWith(DegradedSkeleton(goal));
        }

        // 3. Decompose into a daily checklist.
        var tasks = LastObservation(ctx, TaskDecomposer);
        if (tasks is null && Available(tools, TaskDecomposer))
        {
            return Call(TaskDecomposer, JsonSerializer.SerializeToElement(new { goal }, PlanJson.Options));
        }

        // 4. Assemble the finished plan.
        return FinishWith(Assemble(goal, plan.Result.Output, tasks?.Result));
    }

    private static Observation? LastObservation(WorkContext ctx, string toolName) =>
        ctx.Observations.LastOrDefault(o => o.ToolName == toolName);

    private static bool Available(IReadOnlyList<ToolDescriptor> tools, string name) =>
        tools.Any(t => t.Name == name);

    private static Task<PlanDecision> Call(string tool, JsonElement args) =>
        Task.FromResult<PlanDecision>(new PlanDecision.CallTool(tool, args));

    private static Task<PlanDecision> FinishWith(HealthPlanResult result) =>
        Task.FromResult<PlanDecision>(new PlanDecision.Finish(result.Summary, result.ToJson()));

    private static JsonElement PlanArgs(HealthGoal goal, HealthProfile profile) =>
        JsonSerializer.SerializeToElement(
            new { goal, currentKg = profile.WeightKg, goalKg = profile.GoalWeightKg, targetDays = profile.TargetDays },
            PlanJson.Options);

    private static HealthPlanResult Assemble(HealthGoal goal, JsonElement plan, ToolResult? tasksResult)
    {
        var tasks = tasksResult is { Success: true } && tasksResult.Output.TryGetProperty("tasks", out var t)
            ? t.Deserialize<IReadOnlyList<PlanTask>>(PlanJson.Options) ?? Array.Empty<PlanTask>()
            : Array.Empty<PlanTask>();

        return new HealthPlanResult
        {
            Goal = goal,
            DailyCalorieTarget = plan.GetProperty("dailyCalorieTarget").GetInt32(),
            DailyProteinTargetGrams = plan.GetProperty("dailyProteinTargetGrams").GetInt32(),
            TimelineDays = plan.GetProperty("timelineDays").GetInt32(),
            Summary = plan.GetProperty("summary").GetString() ?? string.Empty,
            Tasks = tasks,
        };
    }

    private static HealthPlanResult DegradedSkeleton(HealthGoal goal) => new()
    {
        Goal = goal,
        Degraded = true,
        Summary = "I couldn't build a full plan safely right now; please review your details or consult a professional.",
        Tasks = new[] { new PlanTask(TaskCategory.Recovery, "Check your profile entries and try again.") },
    };
}
