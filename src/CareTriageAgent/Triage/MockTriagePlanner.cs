using AgentRuntime.Context;
using AgentRuntime.Llm;

namespace CareTriageAgent.Triage;

/// <summary>
/// The deterministic, offline planner (the app's default <see cref="ILlmClient"/>). State-dependent:
/// it reads what's already in the <see cref="WorkContext"/> to decide the next step — call the
/// symptom KB if symptoms aren't scored yet, otherwise classify the score via <see cref="TriagePolicy"/>
/// and finish with a <see cref="TriageResult"/>. No key, no network, reproducible for tests.
/// </summary>
public sealed class MockTriagePlanner : ILlmClient
{
    private const string SymptomKb = "symptom_kb";

    private readonly TriagePolicy _policy;

    public MockTriagePlanner(TriagePolicy policy)
    {
        _policy = policy;
    }

    public Task<PlanDecision> PlanNextStepAsync(WorkContext ctx, IReadOnlyList<ToolDescriptor> tools, CancellationToken ct)
    {
        var kb = ctx.Observations.LastOrDefault(o => o.ToolName == SymptomKb);

        // Symptoms not scored yet and the tool is available -> go score them first.
        if (kb is null && tools.Any(t => t.Name == SymptomKb))
        {
            return Task.FromResult<PlanDecision>(new PlanDecision.CallTool(SymptomKb, default));
        }

        var score = kb?.Result.Output.GetProperty("score").GetInt32() ?? 0;
        var advice = kb?.Result.Output.GetProperty("advice").GetString() ?? string.Empty;
        var urgency = _policy.Classify(score);

        var result = new TriageResult
        {
            Urgency = urgency,
            RecommendedAction = ActionFor(urgency),
            Advice = advice,
            ToolsInvoked = ctx.Observations.Select(o => o.ToolName).Distinct().ToArray(),
        };

        var message = string.IsNullOrEmpty(advice)
            ? ActionFor(urgency)
            : $"{ActionFor(urgency)} {advice}";

        return Task.FromResult<PlanDecision>(new PlanDecision.Finish(message, result.ToJson()));
    }

    private static string ActionFor(UrgencyLevel urgency) => urgency switch
    {
        UrgencyLevel.SelfCare => "Looks self-manageable at home; monitor your symptoms.",
        UrgencyLevel.SeeGp => "Consider booking a routine GP appointment.",
        UrgencyLevel.UrgentCare => "Seek urgent care today.",
        UrgencyLevel.Emergency => "Seek emergency care now.",
        _ => "Consult a healthcare professional.",
    };
}
