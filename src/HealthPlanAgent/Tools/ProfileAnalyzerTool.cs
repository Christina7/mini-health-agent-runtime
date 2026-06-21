using System.Text.Json;
using AgentRuntime.Context;
using AgentRuntime.Tools;
using HealthPlanAgent.Planning;

namespace HealthPlanAgent.Tools;

/// <summary>
/// Pure, stateless calculator (the plan-side mirror of <c>symptom_kb</c>): BMR + TDEE + BMI from the
/// submitted profile, plus informational <c>flags</c>. Rounding is pinned here — whole-calorie BMR/TDEE,
/// 1-dp BMI — so the serialized JSON is deterministic for tests. Junk input (e.g. HeightCm=0) returns
/// <c>Success:false</c> rather than throwing, so the planner degrades cleanly. The flags are hints for
/// <c>plan_generator</c>, never a safety gate; safety lives in the guardrail and PlanMath.
/// </summary>
public sealed class ProfileAnalyzerTool : ITool
{
    public string Name => "profile_analyzer";
    public string Description => "Computes BMR, TDEE, and BMI from the user's profile.";

    public JsonElement InputSchema { get; } =
        JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

    public Task<ToolResult> ExecuteAsync(JsonElement args, WorkContext ctx, CancellationToken ct)
    {
        HealthProfile? profile = null;
        if (args.ValueKind == JsonValueKind.Object)
        {
            try
            {
                profile = args.Deserialize<HealthProfile>(PlanJson.Options);
            }
            catch (JsonException)
            {
                profile = null;
            }
        }

        if (profile is null || !PlanMath.IsViable(profile))
        {
            return Task.FromResult(new ToolResult(Success: false, Output: default, Error: "invalid or incomplete profile"));
        }

        var bmr = (int)Math.Round(PlanMath.Bmr(profile));
        var tdee = (int)Math.Round(PlanMath.Tdee(profile));
        var bmi = Math.Round(PlanMath.Bmi(profile), 1);

        var flags = new List<string>();
        if (bmi < 18.5) flags.Add("underweight_bmi");
        if (bmi >= 30) flags.Add("obese_bmi");

        var output = JsonSerializer.SerializeToElement(new { bmr, tdee, bmi, flags }, PlanJson.Options);
        return Task.FromResult(new ToolResult(Success: true, Output: output));
    }
}
