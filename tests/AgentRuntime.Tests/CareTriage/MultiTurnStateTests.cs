using System.Text.Json;
using AgentRuntime.Context;
using AgentRuntime.Failure;
using AgentRuntime.Orchestration;
using AgentRuntime.Tools;
using CareTriageAgent.Tools;
using CareTriageAgent.Triage;

namespace AgentRuntime.Tests.CareTriage;

/// <summary>
/// Pins the per-turn boundary of the working state. A <see cref="WorkContext"/> persists conversation
/// History across turns, but the observations gathered and the degraded flag are scoped to a single
/// turn — otherwise a follow-up message would be triaged against the previous turn's tool output.
/// (Regression: a follow-up "dizzy with abdominal pain" was reusing an earlier turn's lower score.)
/// </summary>
public class MultiTurnStateTests
{
    private static readonly SymptomEntry[] Kb =
    {
        new("sore_throat", new[] { "sore throat" }, BaseSeverity: 1, SelfCareAdvice: "Rest and fluids."),
        new("breathing_difficulty", new[] { "trouble breathing" }, BaseSeverity: 8, SelfCareAdvice: "Get assessed promptly."),
    };

    private static TriagePolicy Policy() =>
        new(new TriageThresholds(SelfCareMaxScore: 2, SeeGpMaxScore: 5, UrgentCareMaxScore: 8));

    // A new symptom on a later turn is re-scored, not served from the prior turn's observation.
    [Fact]
    public async Task Each_turn_rescores_the_latest_symptoms()
    {
        var registry = new ToolRegistry(new ITool[]
        {
            new SymptomExtractorTool(new KeywordSymptomExtractor(Kb)),
            new SymptomKnowledgeBaseTool(Kb),
        });
        var orchestrator = new AgentOrchestrator(new MockTriagePlanner(Policy()), registry);
        var ctx = new WorkContext("conv-1");

        var first = await orchestrator.RunTurnAsync(ctx, "sore throat", CancellationToken.None);
        Assert.Equal(UrgencyLevel.SelfCare, TriageResult.FromJson(first.Result!.Value).Urgency); // score 1

        var second = await orchestrator.RunTurnAsync(ctx, "sore throat and trouble breathing", CancellationToken.None);
        Assert.Equal(UrgencyLevel.Emergency, TriageResult.FromJson(second.Result!.Value).Urgency); // score 9, re-scored
    }

    // A degraded turn doesn't poison a later healthy turn: once the tool recovers, the next turn is
    // classified normally and is not marked degraded.
    [Fact]
    public async Task A_degraded_turn_does_not_leak_into_the_next_turn()
    {
        var registry = new ToolRegistry(new ITool[]
        {
            new SymptomExtractorTool(new KeywordSymptomExtractor(Kb)),
            new FailOnceTool(),
        });
        var orchestrator = new AgentOrchestrator(new MockTriagePlanner(Policy()), registry, guardrails: null, scope: new ExecutionScope(maxRetries: 0));
        var ctx = new WorkContext("conv-1");

        var first = await orchestrator.RunTurnAsync(ctx, "sore throat", CancellationToken.None);
        Assert.True(first.Degraded); // the tool failed this turn

        var second = await orchestrator.RunTurnAsync(ctx, "sore throat", CancellationToken.None);
        Assert.False(second.Degraded); // the tool recovered; the prior degrade must not carry over
        Assert.Equal(UrgencyLevel.SelfCare, TriageResult.FromJson(second.Result!.Value).Urgency);
    }

    /// <summary>A symptom_kb tool that throws on its first call and succeeds thereafter.</summary>
    private sealed class FailOnceTool : ITool
    {
        private int _calls;

        public string Name => "symptom_kb";
        public string Description => "Fails on the first call, then succeeds.";
        public JsonElement InputSchema { get; } = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

        public Task<ToolResult> ExecuteAsync(JsonElement args, WorkContext ctx, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                throw new InvalidOperationException("transient symptom KB failure");
            }

            var output = JsonSerializer.SerializeToElement(new { score = 1, advice = "Rest and fluids.", matched = new[] { "sore_throat" } });
            return Task.FromResult(new ToolResult(Success: true, Output: output));
        }
    }
}
