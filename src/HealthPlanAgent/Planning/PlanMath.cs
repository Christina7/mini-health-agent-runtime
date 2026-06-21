namespace HealthPlanAgent.Planning;

/// <summary>
/// The body-energy formulas, defined <b>once</b> so the <c>profile_analyzer</c> tool and the
/// (later) <c>UnsafeGoalGuardrail</c> can never disagree about the BMR floor. Pure doubles — rounding
/// is pinned at the tool boundary, not here, so callers control precision.
/// </summary>
public static class PlanMath
{
    /// <summary>A profile we can compute on without dividing by zero or producing nonsense.</summary>
    public static bool IsViable(HealthProfile p) =>
        p.AgeYears > 0 && p.WeightKg > 0 && p.HeightCm > 0;

    /// <summary>Basal metabolic rate, Mifflin–St Jeor.</summary>
    public static double Bmr(HealthProfile p)
    {
        var sexOffset = p.Sex == Sex.Male ? 5 : -161;
        return (10 * p.WeightKg) + (6.25 * p.HeightCm) - (5 * p.AgeYears) + sexOffset;
    }

    /// <summary>Total daily energy expenditure: BMR scaled by the activity multiplier.</summary>
    public static double Tdee(HealthProfile p) => Bmr(p) * ActivityFactor(p.ActivityLevel);

    /// <summary>Body-mass index from the metric profile.</summary>
    public static double Bmi(HealthProfile p)
    {
        var heightM = p.HeightCm / 100.0;
        return p.WeightKg / (heightM * heightM);
    }

    private static double ActivityFactor(ActivityLevel level) => level switch
    {
        ActivityLevel.Sedentary => 1.2,
        ActivityLevel.Light => 1.375,
        ActivityLevel.Moderate => 1.55,
        ActivityLevel.Active => 1.725,
        ActivityLevel.VeryActive => 1.9,
        _ => 1.2,
    };
}
