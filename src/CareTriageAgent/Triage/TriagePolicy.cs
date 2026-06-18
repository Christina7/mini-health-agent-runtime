namespace CareTriageAgent.Triage;

/// <summary>How urgently the user should seek care, from lowest to highest.</summary>
public enum UrgencyLevel
{
    SelfCare,
    SeeGp,
    UrgentCare,
    Emergency
}

/// <summary>
/// Inclusive upper score bounds for each non-emergency band. A score above the urgent-care
/// bound is an emergency. Sourced from RuntimeConfig in a later slice; passed in directly for now.
/// </summary>
public sealed record TriageThresholds(int SelfCareMaxScore, int SeeGpMaxScore, int UrgentCareMaxScore);

/// <summary>
/// Maps a symptom severity score to an <see cref="UrgencyLevel"/>. Pure and deterministic — the
/// same score always yields the same band — so the recommendation is reproducible and testable.
/// </summary>
public sealed class TriagePolicy
{
    private readonly TriageThresholds _thresholds;

    public TriagePolicy(TriageThresholds thresholds)
    {
        _thresholds = thresholds;
    }

    public UrgencyLevel Classify(int score)
    {
        if (score <= _thresholds.SelfCareMaxScore) return UrgencyLevel.SelfCare;
        if (score <= _thresholds.SeeGpMaxScore) return UrgencyLevel.SeeGp;
        if (score <= _thresholds.UrgentCareMaxScore) return UrgencyLevel.UrgentCare;
        return UrgencyLevel.Emergency;
    }
}
