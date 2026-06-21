namespace HealthPlanAgent.Planning;

/// <summary>The prescription <c>plan_generator</c> produces from a goal, TDEE, and the policy.</summary>
public sealed record PlanPrescription(
    int DailyCalorieTarget,
    int DailyProteinTargetGrams,
    int TimelineDays,
    string Summary);

/// <summary>
/// Turns maintenance energy (TDEE) into safe daily targets. The intensity <b>cap</b> is what a config
/// flight varies (aggressive vs conservative) — goal weight and target days set the requested pace, but
/// the deficit is capped so the plan is safe by construction, and the calorie target never falls below
/// <see cref="CalorieFloor"/>. The achievable timeline is reported back (it may exceed the requested
/// TargetDays). Pure and deterministic, mirroring the triage side's policy.
/// </summary>
public sealed record PlanPolicy(double DeficitCapFraction, double ProteinGramsPerKg, int CalorieFloor)
{
    private const double KcalPerKg = 7700;

    public static PlanPolicy Default { get; } =
        new(DeficitCapFraction: 0.20, ProteinGramsPerKg: 1.8, CalorieFloor: 1200);

    public PlanPrescription Prescribe(HealthGoal goal, double tdee, double currentKg, double? goalKg, int targetDays)
    {
        if (goal != HealthGoal.LoseFat)
        {
            return Maintenance(goal, tdee, currentKg, targetDays);
        }

        var target = goalKg ?? currentKg;
        var toLose = Math.Max(0, currentKg - target);
        var requestedDeficit = targetDays > 0 ? toLose * KcalPerKg / targetDays : 0;
        var cappedDeficit = Math.Min(requestedDeficit, DeficitCapFraction * tdee);

        var calories = Math.Max((int)Math.Round(tdee - cappedDeficit), CalorieFloor);
        var actualDeficit = tdee - calories;
        var timeline = actualDeficit > 0 ? (int)Math.Ceiling(toLose * KcalPerKg / actualDeficit) : targetDays;
        var protein = (int)Math.Round(ProteinGramsPerKg * target);

        var summary = toLose > 0
            ? $"Eat ~{calories} kcal/day (~{protein} g protein) to reach {target:0.#} kg in about {timeline} days."
            : $"Maintain ~{calories} kcal/day — you're already at your goal weight.";

        return new PlanPrescription(calories, protein, timeline, summary);
    }

    private PlanPrescription Maintenance(HealthGoal goal, double tdee, double currentKg, int targetDays)
    {
        var calories = (int)Math.Round(tdee);
        var protein = (int)Math.Round(ProteinGramsPerKg * currentKg);
        var summary = goal == HealthGoal.ImproveSleep
            ? $"Maintain ~{calories} kcal/day and a consistent routine to support sleep."
            : $"Maintain ~{calories} kcal/day with balanced meals to support energy.";
        return new PlanPrescription(calories, protein, targetDays, summary);
    }
}
