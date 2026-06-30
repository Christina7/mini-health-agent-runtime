namespace CareTriageAgent.Triage;

/// <summary>
/// Turns free user text into a normalized list of known symptoms (NLU). This is a CareTriageAgent
/// domain seam — deliberately separate from the runtime's <c>ILlmClient</c> planner seam: the planner
/// decides which tool to call next, whereas an extractor only proposes which taxonomy symptoms the
/// text mentions. The deterministic core still disposes the urgency (scoring + policy + guardrail).
///
/// Implementations: a keyword (substring) extractor for the offline/mock path, and later an LLM-backed
/// one behind <c>llmProvider=anthropic</c>. An extractor never throws on a provider failure — it falls
/// back internally and reports <see cref="SymptomExtraction.Fallback"/> = true (see the spec §4).
/// </summary>
public interface ISymptomExtractor
{
    Task<SymptomExtraction> ExtractAsync(string userText, CancellationToken ct);
}

/// <summary>
/// The result of extraction: which known symptoms were found, which provider produced it, and whether
/// it came from the internal fallback rather than the primary provider.
/// </summary>
public sealed record SymptomExtraction(
    IReadOnlyList<ExtractedSymptom> Symptoms,
    string Provider,
    bool Fallback);

/// <summary>One taxonomy symptom and whether the text indicates it is present.</summary>
public sealed record ExtractedSymptom(string Id, bool Present);
