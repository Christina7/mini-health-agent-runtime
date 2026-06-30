using System.Text.Json;
using AgentRuntime.Context;
using AgentRuntime.Llm;

namespace CareTriageAgent.Triage;

/// <summary>
/// The deterministic, offline planner (the app's default <see cref="ILlmClient"/>). State-dependent:
/// it reads what's already in the <see cref="WorkContext"/> to decide the next step, driving a
/// two-step pipeline — first <c>symptom_extractor</c> to find which known symptoms are present, then
/// <c>symptom_kb</c> to score those IDs — before classifying via <see cref="TriagePolicy"/> and
/// finishing with a <see cref="TriageResult"/>. No key, no network, reproducible for tests.
///
/// Safety (spec §5.4): if either stage is unavailable or fails, the planner does <b>not</b> fabricate
/// a score-0 SelfCare from an un-assessed turn — that is the #36 unsafe direction. It degrades to a
/// clearly-marked SeeGp instead. A genuine score of 0 (the pipeline ran and found nothing) is still
/// SelfCare; "couldn't assess" and "assessed, nothing found" are deliberately distinct.
/// </summary>
public sealed class MockTriagePlanner : ILlmClient
{
    private const string SymptomExtractor = "symptom_extractor";
    private const string SymptomKb = "symptom_kb";

    private readonly TriagePolicy _policy;

    public MockTriagePlanner(TriagePolicy policy)
    {
        _policy = policy;
    }

    public Task<PlanDecision> PlanNextStepAsync(WorkContext ctx, IReadOnlyList<ToolDescriptor> tools, CancellationToken ct)
    {
        var extractorAvailable = tools.Any(t => t.Name == SymptomExtractor);
        var kbAvailable = tools.Any(t => t.Name == SymptomKb);

        // Both stages are required to assess a turn. If either is disabled, we can't actually evaluate
        // the symptoms — degrade safely rather than assert an unverified SelfCare (issue #36).
        if (!extractorAvailable || !kbAvailable)
        {
            return Degrade(ctx);
        }

        var extraction = ctx.Observations.LastOrDefault(o => o.ToolName == SymptomExtractor);

        // Step 1: not extracted yet -> extract the present symptoms first.
        if (extraction is null)
        {
            return Task.FromResult<PlanDecision>(new PlanDecision.CallTool(SymptomExtractor, default));
        }

        // Extraction itself failed/degraded -> we have no reliable symptom set; degrade safely.
        if (ctx.Degraded || extraction is { Result.Success: false })
        {
            return Degrade(ctx);
        }

        var kb = ctx.Observations.LastOrDefault(o => o.ToolName == SymptomKb);

        // Step 2: extracted but not scored yet -> score the present IDs (passed as explicit args).
        if (kb is null)
        {
            var args = BuildScorerArgs(extraction.Result.Output);
            return Task.FromResult<PlanDecision>(new PlanDecision.CallTool(SymptomKb, args));
        }

        // The scorer failed and the runtime degraded the turn: don't assert an urgency we couldn't
        // actually assess — return a safe, clearly-degraded recommendation.
        if (ctx.Degraded || kb is { Result.Success: false })
        {
            return Degrade(ctx);
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

        return Task.FromResult<PlanDecision>(new PlanDecision.Finish(message, result.ToJson(), Summary: urgency.ToString()));
    }

    // The safe fallback when the turn could not be assessed (a stage disabled or failed).
    private Task<PlanDecision> Degrade(WorkContext ctx)
    {
        var degraded = new TriageResult
        {
            Urgency = UrgencyLevel.SeeGp,
            RecommendedAction = "I couldn't verify your symptoms against the guidance right now; please consult a healthcare professional.",
            ToolsInvoked = ctx.Observations.Select(o => o.ToolName).Distinct().ToArray(),
            Degraded = true,
        };
        return Task.FromResult<PlanDecision>(
            new PlanDecision.Finish(degraded.RecommendedAction, degraded.ToJson(), Summary: "SeeGp · degraded"));
    }

    // Turns the extractor's output into the scorer's input: the IDs marked present.
    private static JsonElement BuildScorerArgs(JsonElement extractorOutput)
    {
        var presentIds = new List<string>();
        if (extractorOutput.ValueKind == JsonValueKind.Object
            && extractorOutput.TryGetProperty("symptoms", out var symptoms)
            && symptoms.ValueKind == JsonValueKind.Array)
        {
            foreach (var symptom in symptoms.EnumerateArray())
            {
                if (symptom.TryGetProperty("present", out var present)
                    && present.ValueKind == JsonValueKind.True
                    && symptom.TryGetProperty("id", out var id)
                    && id.GetString() is { } s)
                {
                    presentIds.Add(s);
                }
            }
        }

        return JsonSerializer.SerializeToElement(new { presentIds = presentIds.ToArray() });
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
