using System.Text.Json;
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

        // The symptom lookup failed and the runtime degraded the turn: don't assert an urgency we
        // couldn't actually assess — return a safe, clearly-degraded recommendation.
        if (ctx.Degraded || kb is { Result.Success: false })
        {
            var degraded = new TriageResult
            {
                Urgency = UrgencyLevel.SeeGp,
                RecommendedAction = "I couldn't verify your symptoms against the guidance right now; please consult a healthcare professional.",
                ToolsInvoked = ctx.Observations.Select(o => o.ToolName).Distinct().ToArray(),
                Degraded = true,
            };
            return Task.FromResult<PlanDecision>(new PlanDecision.Finish(degraded.RecommendedAction, degraded.ToJson()));
        }

        var score = ReadInt(kb?.Result.Output, "score");
        var advice = ReadString(kb?.Result.Output, "advice");
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

    private static int ReadInt(JsonElement? output, string property) =>
        output is { ValueKind: JsonValueKind.Object } el && el.TryGetProperty(property, out var p) && p.TryGetInt32(out var v)
            ? v
            : 0;

    private static string ReadString(JsonElement? output, string property) =>
        output is { ValueKind: JsonValueKind.Object } el && el.TryGetProperty(property, out var p)
            ? p.GetString() ?? string.Empty
            : string.Empty;

    private static string ActionFor(UrgencyLevel urgency) => urgency switch
    {
        UrgencyLevel.SelfCare => "Looks self-manageable at home; monitor your symptoms.",
        UrgencyLevel.SeeGp => "Consider booking a routine GP appointment.",
        UrgencyLevel.UrgentCare => "Seek urgent care today.",
        UrgencyLevel.Emergency => "Seek emergency care now.",
        _ => "Consult a healthcare professional.",
    };
}
