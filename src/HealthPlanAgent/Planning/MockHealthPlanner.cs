using System.Text.Json;
using AgentRuntime.Context;
using AgentRuntime.Llm;
using AgentRuntime.Tools;

namespace HealthPlanAgent.Planning;

/// <summary>
/// The deterministic, offline planner for the plan agent (the default <see cref="ILlmClient"/>).
/// State-dependent like the triage planner, but it reads its input from the session-scoped
/// <see cref="TurnInputHolder"/> (never from the user message, which stays human-readable). It walks
/// the next un-run tool in the chain for the turn's action:
/// <list type="bullet">
/// <item>Create: profile_analyzer → plan_generator → task_decomposer → finish.</item>
/// <item>Log: nutrition_calculator → progress_evaluator → finish (appending a progress entry).</item>
/// </list>
/// A failed/disabled tool yields a safe, clearly-degraded result rather than a crash, and a degraded
/// log turn preserves the prior plan instead of corrupting it.
/// </summary>
public sealed class MockHealthPlanner : ILlmClient
{
    private const string ProfileAnalyzer = "profile_analyzer";
    private const string PlanGenerator = "plan_generator";
    private const string TaskDecomposer = "task_decomposer";
    private const string NutritionCalculator = "nutrition_calculator";
    private const string ProgressEvaluator = "progress_evaluator";

    private readonly TurnInputHolder _holder;

    public MockHealthPlanner(TurnInputHolder holder) => _holder = holder;

    public Task<PlanDecision> PlanNextStepAsync(WorkContext ctx, IReadOnlyList<ToolDescriptor> tools, CancellationToken ct)
    {
        var envelope = _holder.Current;
        return envelope?.Action == PlanAction.Log
            ? LogStep(ctx, tools, envelope)
            : CreateStep(ctx, tools, envelope);
    }

    private Task<PlanDecision> CreateStep(WorkContext ctx, IReadOnlyList<ToolDescriptor> tools, PlanEnvelope? envelope)
    {
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

    private Task<PlanDecision> LogStep(WorkContext ctx, IReadOnlyList<ToolDescriptor> tools, PlanEnvelope envelope)
    {
        var prior = _holder.PriorArtifact;
        var log = envelope.Log;

        // Nothing to log against → degrade safely.
        if (prior is null || log is null)
        {
            return FinishWith(DegradedSkeleton(prior?.Goal ?? HealthGoal.LoseFat));
        }

        // 1. Score the day's calories against the plan target.
        var nutrition = LastObservation(ctx, NutritionCalculator);
        if (nutrition is null && Available(tools, NutritionCalculator))
        {
            return Call(NutritionCalculator, JsonSerializer.SerializeToElement(
                new { calorieTarget = prior.DailyCalorieTarget, caloriesLogged = log.CaloriesLogged }, PlanJson.Options));
        }

        // A failed/degraded nutrition step must not corrupt the plan — return it untouched, flagged.
        if (ctx.Degraded || nutrition is null or { Result.Success: false })
        {
            return FinishWith(prior with { Degraded = true });
        }

        // 2. Turn the day into a progress entry.
        var eval = LastObservation(ctx, ProgressEvaluator);
        if (eval is null && Available(tools, ProgressEvaluator))
        {
            return Call(ProgressEvaluator, JsonSerializer.SerializeToElement(
                new { day = prior.Progress.Count + 1, tasksCompleted = log.TasksCompleted, tasksTotal = prior.Tasks.Count },
                PlanJson.Options));
        }

        if (eval is null or { Result.Success: false })
        {
            return FinishWith(prior with { Degraded = true });
        }

        // 3. Append it to the living artifact.
        var (updated, note) = AppendProgress(prior, eval.Result.Output);
        return FinishWith(updated, note);
    }

    private static Observation? LastObservation(WorkContext ctx, string toolName) =>
        ctx.Observations.LastOrDefault(o => o.ToolName == toolName);

    private static bool Available(IReadOnlyList<ToolDescriptor> tools, string name) =>
        tools.Any(t => t.Name == name);

    private static Task<PlanDecision> Call(string tool, JsonElement args) =>
        Task.FromResult<PlanDecision>(new PlanDecision.CallTool(tool, args));

    private static Task<PlanDecision> FinishWith(HealthPlanResult result, string? message = null) =>
        Task.FromResult<PlanDecision>(new PlanDecision.Finish(message ?? result.Summary, result.ToJson()));

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

    private static (HealthPlanResult Updated, string? Note) AppendProgress(HealthPlanResult prior, JsonElement entryJson)
    {
        var entry = entryJson.Deserialize<ProgressEntry>(PlanJson.Options);
        if (entry is null)
        {
            return (prior with { Degraded = true }, null);
        }

        var progress = prior.Progress.Append(entry).ToArray();
        return (prior with { Progress = progress, Degraded = false }, entry.Note);
    }

    private static HealthPlanResult DegradedSkeleton(HealthGoal goal) => new()
    {
        Goal = goal,
        Degraded = true,
        Summary = "I couldn't build a full plan safely right now; please review your details or consult a professional.",
        Tasks = new[] { new PlanTask(TaskCategory.Recovery, "Check your profile entries and try again.") },
    };
}
