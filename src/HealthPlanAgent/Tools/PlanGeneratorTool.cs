using System.Text.Json;
using AgentRuntime.Context;
using AgentRuntime.Tools;
using HealthPlanAgent.Planning;

namespace HealthPlanAgent.Tools;

/// <summary>
/// Turns the analysis into a prescription. It <b>consumes</b> <c>profile_analyzer</c>'s TDEE from the
/// turn's observations (the create chain's load-bearing link) rather than recomputing it — so with no
/// upstream analysis it degrades (<c>Success:false</c>) instead of silently going it alone. The
/// intensity cap lives in <see cref="PlanPolicy"/>, so a config flight can move the calorie target
/// without a recompile.
/// </summary>
public sealed class PlanGeneratorTool : ITool
{
    private readonly PlanPolicy _policy;

    public PlanGeneratorTool(PlanPolicy policy) => _policy = policy;

    public string Name => "plan_generator";
    public string Description => "Produces calorie/protein targets and a timeline from the goal and the profile analysis.";

    public JsonElement InputSchema { get; } =
        JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

    public Task<ToolResult> ExecuteAsync(JsonElement args, WorkContext ctx, CancellationToken ct)
    {
        var analysis = ctx.Observations.LastOrDefault(o => o.ToolName == "profile_analyzer");
        if (analysis is not { Result.Success: true } || analysis.Result.Output.ValueKind != JsonValueKind.Object
            || !analysis.Result.Output.TryGetProperty("tdee", out var tdeeEl))
        {
            return Task.FromResult(new ToolResult(Success: false, Output: default, Error: "no profile analysis to build on"));
        }

        var tdee = tdeeEl.GetDouble();
        var goal = args.GetProperty("goal").Deserialize<HealthGoal>(PlanJson.Options);
        var currentKg = args.GetProperty("currentKg").GetDouble();
        var targetDays = args.GetProperty("targetDays").GetInt32();
        double? goalKg = args.TryGetProperty("goalKg", out var gk) && gk.ValueKind == JsonValueKind.Number
            ? gk.GetDouble()
            : null;

        var prescription = _policy.Prescribe(goal, tdee, currentKg, goalKg, targetDays);
        var output = JsonSerializer.SerializeToElement(prescription, PlanJson.Options);
        return Task.FromResult(new ToolResult(Success: true, Output: output));
    }
}
