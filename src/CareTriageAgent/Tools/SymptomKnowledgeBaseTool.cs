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
/// A pure scorer over a knowledge base. Given the present symptom IDs (supplied as the
/// <c>presentIds</c> argument by the planner, sourced from <c>symptom_extractor</c>), it sums those
/// symptoms' base severities into a single score and collects their self-care advice. It does
/// <b>not</b> read the raw user text — recognising symptoms in free text is the extractor's job, so
/// the two stages stay decoupled (spec §5.3). Returns JSON: { score, advice, matched[] }.
/// </summary>
public sealed class SymptomKnowledgeBaseTool : ITool
{
    private readonly IReadOnlyDictionary<string, SymptomEntry> _byId;

    public SymptomKnowledgeBaseTool(IReadOnlyList<SymptomEntry> knowledgeBase)
    {
        _byId = knowledgeBase.ToDictionary(entry => entry.Id);
    }

    public string Name => "symptom_kb";
    public string Description => "Scores a set of present symptom IDs into a severity score and self-care advice.";

    public JsonElement InputSchema { get; } =
        JsonDocument.Parse("""{"type":"object","properties":{"presentIds":{"type":"array","items":{"type":"string"}}}}""").RootElement.Clone();

    public Task<ToolResult> ExecuteAsync(JsonElement args, WorkContext ctx, CancellationToken ct)
    {
        var matched = ReadPresentIds(args)
            .Where(_byId.ContainsKey)
            .Select(id => _byId[id])
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

    private static IEnumerable<string> ReadPresentIds(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("presentIds", out var ids)
            && ids.ValueKind == JsonValueKind.Array)
        {
            foreach (var id in ids.EnumerateArray())
            {
                if (id.ValueKind == JsonValueKind.String && id.GetString() is { } s)
                {
                    yield return s;
                }
            }
        }
    }
}
