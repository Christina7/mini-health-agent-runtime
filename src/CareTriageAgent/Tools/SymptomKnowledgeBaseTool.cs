using System.Text.Json;
using AgentRuntime.Context;
using AgentRuntime.Tools;

namespace CareTriageAgent.Tools;

/// <summary>
/// One symptom in the knowledge base: the keywords that identify it, a base severity score, and
/// the self-care advice to surface when it's present.
/// </summary>
public sealed record SymptomEntry(string Id, IReadOnlyList<string> Keywords, int BaseSeverity, string SelfCareAdvice);

/// <summary>
/// Scores the user's described symptoms against a knowledge base. Matched symptoms' severities
/// are summed into a single score (so more/worse symptoms raise it) and their advice is collected.
/// Returns JSON: { score, advice, matched[] }. Deterministic and offline.
/// </summary>
public sealed class SymptomKnowledgeBaseTool : ITool
{
    private readonly IReadOnlyList<SymptomEntry> _knowledgeBase;

    public SymptomKnowledgeBaseTool(IReadOnlyList<SymptomEntry> knowledgeBase)
    {
        _knowledgeBase = knowledgeBase;
    }

    public string Name => "symptom_kb";
    public string Description => "Looks up self-care guidance and a severity score for described symptoms.";

    public JsonElement InputSchema { get; } =
        JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

    public Task<ToolResult> ExecuteAsync(JsonElement args, WorkContext ctx, CancellationToken ct)
    {
        var text = ctx.LatestUserText;

        var matched = _knowledgeBase
            .Where(entry => entry.Keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var score = matched.Sum(entry => entry.BaseSeverity);
        var advice = matched.Count > 0
            ? string.Join(" ", matched.Select(entry => entry.SelfCareAdvice))
            : "No specific guidance found for the described symptoms.";

        var output = JsonSerializer.SerializeToElement(new
        {
            score,
            advice,
            matched = matched.Select(entry => entry.Id).ToArray(),
        });

        return Task.FromResult(new ToolResult(Success: true, Output: output));
    }
}
