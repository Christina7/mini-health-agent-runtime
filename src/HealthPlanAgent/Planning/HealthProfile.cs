namespace HealthPlanAgent.Planning;

public enum Sex
{
    Male,
    Female
}

/// <summary>Activity level, mapped to a TDEE multiplier in <see cref="PlanMath"/>.</summary>
public enum ActivityLevel
{
    Sedentary,
    Light,
    Moderate,
    Active,
    VeryActive
}

/// <summary>
/// The user's submitted profile — <b>metric only</b> (the browser converts imperial → kg/cm before
/// POST, so the tested core never sees a unit flag). <see cref="GoalWeightKg"/> is required only for a
/// <c>LoseFat</c> goal; sleep/energy goals leave it null.
/// </summary>
public sealed record HealthProfile(
    int AgeYears,
    Sex Sex,
    double WeightKg,
    double HeightCm,
    ActivityLevel ActivityLevel,
    int TargetDays,
    double? GoalWeightKg = null);
