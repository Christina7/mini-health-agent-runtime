using System.Text.Json;
using AgentRuntime.Context;
using AgentRuntime.Tools;
using CareTriageAgent.Triage;

namespace CareTriageAgent.Tools;

/// <summary>
/// The <c>symptom_extractor</c> tool: a thin <see cref="ITool"/> that delegates to an injected
/// <see cref="ISymptomExtractor"/> and serializes the result to its output. The planner calls it
/// before <c>symptom_kb</c>; the present symptom IDs it returns are passed on as the scorer's input,
/// making triage a genuine multi-step pipeline (extract → score → classify).
///
/// Returns JSON: { symptoms: [{ id, present }], provider, fallback }. Because the extractor absorbs
/// provider failures internally (returning a successful fallback), this tool reports
/// <see cref="ToolResult.Success"/> = true unless the extractor itself throws.
/// </summary>
public sealed class SymptomExtractorTool : ITool
{
    private readonly ISymptomExtractor _extractor;

    public SymptomExtractorTool(ISymptomExtractor extractor)
    {
        _extractor = extractor;
    }

    public string Name => "symptom_extractor";
    public string Description => "Identifies which known symptoms are present in the user's description.";

    public JsonElement InputSchema { get; } =
        JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

    public async Task<ToolResult> ExecuteAsync(JsonElement args, WorkContext ctx, CancellationToken ct)
    {
        var extraction = await _extractor.ExtractAsync(ctx.LatestUserText, ct);

        var output = JsonSerializer.SerializeToElement(new
        {
            symptoms = extraction.Symptoms.Select(s => new { id = s.Id, present = s.Present }).ToArray(),
            provider = extraction.Provider,
            fallback = extraction.Fallback,
        });

        var presentCount = extraction.Symptoms.Count(s => s.Present);
        return new ToolResult(Success: true, Output: output, Summary: $"{presentCount} present");
    }
}
