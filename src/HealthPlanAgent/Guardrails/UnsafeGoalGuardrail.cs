using AgentRuntime.Context;
using AgentRuntime.Orchestration;
using HealthPlanAgent.Planning;

namespace HealthPlanAgent.Guardrails;

/// <summary>
/// The plan-side implementation of the runtime's <see cref="IGuardrail"/>, the mirror of triage's
/// red-flag rule. Registered unconditionally by <see cref="HealthPlanSession"/>; no config/flight can
/// disable it. It reads the <b>typed</b> envelope from the session-scoped <see cref="TurnInputHolder"/>
/// — not the message text — so it keys off the declared goal, not numbers in a re-injected plan. Only a
/// <c>LoseFat</c> create turn can be unsafe; a log turn carries no profile, so the guardrail passes.
/// The safety thresholds are constants here, never config, so configuration can never override safety.
/// </summary>
public sealed class UnsafeGoalGuardrail : IGuardrail
{
    private const double UnderweightBmi = 18.5;
    private const int CrashIntakeFloorKcal = 1200;
    private const double KcalPerKg = 7700;

    private const string Referral =
        "This goal could be unsafe to pursue alone. Please consult a doctor or dietitian before starting a weight-loss plan.";

    private readonly TurnInputHolder _holder;

    public UnsafeGoalGuardrail(TurnInputHolder holder) => _holder = holder;

    public Task<GuardrailVerdict> EvaluateAsync(WorkContext ctx, CancellationToken ct)
    {
        var envelope = _holder.Current;

        // Only a LoseFat goal carrying a profile can be unsafe. Sleep/energy goals and log turns pass.
        if (envelope?.Goal != HealthGoal.LoseFat || envelope.Profile is not { } profile || !PlanMath.IsViable(profile))
        {
            return Task.FromResult(GuardrailVerdict.Pass);
        }

        // 1. Already underweight — losing fat is the wrong goal.
        if (PlanMath.Bmi(profile) < UnderweightBmi)
        {
            return Escalate();
        }

        // 2. The goal weight itself is underweight — an eating-disorder signal.
        if (profile.GoalWeightKg is { } goalKg && PlanMath.BmiAt(goalKg, profile.HeightCm) < UnderweightBmi)
        {
            return Escalate();
        }

        // 3. Reaching the goal by the requested date demands an intake below the safe floor — a crash diet.
        if (profile.GoalWeightKg is { } target && profile.TargetDays > 0)
        {
            var toLose = Math.Max(0, profile.WeightKg - target);
            var requiredDeficit = toLose * KcalPerKg / profile.TargetDays;
            if (PlanMath.Tdee(profile) - requiredDeficit < CrashIntakeFloorKcal)
            {
                return Escalate();
            }
        }

        return Task.FromResult(GuardrailVerdict.Pass);
    }

    private static Task<GuardrailVerdict> Escalate() =>
        Task.FromResult(GuardrailVerdict.ShortCircuitWith(Referral));
}
