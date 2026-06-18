using CareTriageAgent.Triage;

namespace AgentRuntime.Tests.CareTriage;

public class TriagePolicyTests
{
    // Thresholds mirror config defaults: self-care <= 2, see-GP <= 5, urgent-care <= 8, else emergency.
    private static TriagePolicy Policy() => new(new TriageThresholds(SelfCareMaxScore: 2, SeeGpMaxScore: 5, UrgentCareMaxScore: 8));

    // Slice 5 (domain): a pure score -> urgency mapping. Table-driven over each band and its
    // boundary, so an off-by-one in the thresholds is caught.
    [Theory]
    [InlineData(0, UrgencyLevel.SelfCare)]
    [InlineData(2, UrgencyLevel.SelfCare)]    // upper edge of self-care
    [InlineData(3, UrgencyLevel.SeeGp)]       // first see-GP
    [InlineData(5, UrgencyLevel.SeeGp)]       // upper edge of see-GP
    [InlineData(6, UrgencyLevel.UrgentCare)]  // first urgent-care
    [InlineData(8, UrgencyLevel.UrgentCare)]  // upper edge of urgent-care
    [InlineData(9, UrgencyLevel.Emergency)]   // first emergency
    [InlineData(100, UrgencyLevel.Emergency)]
    public void Classify_maps_score_to_urgency_band(int score, UrgencyLevel expected)
    {
        Assert.Equal(expected, Policy().Classify(score));
    }
}
