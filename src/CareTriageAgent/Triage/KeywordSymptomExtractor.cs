using CareTriageAgent.Tools;

namespace CareTriageAgent.Triage;

/// <summary>
/// The offline/mock <see cref="ISymptomExtractor"/>: the substring matching that used to live inside
/// <see cref="SymptomKnowledgeBaseTool"/>. A taxonomy symptom is present when any of its keywords
/// appears in the user text. Deterministic, no network — the default under <c>llmProvider=mock</c>.
///
/// In this slice it only reports the symptoms it finds (all <c>Present = true</c>); negation
/// ("no chest pain") is handled in a later slice. It never throws, so <see cref="SymptomExtraction.Fallback"/>
/// is always false here.
/// </summary>
public sealed class KeywordSymptomExtractor : ISymptomExtractor
{
    private readonly IReadOnlyList<SymptomEntry> _knowledgeBase;

    public KeywordSymptomExtractor(IReadOnlyList<SymptomEntry> knowledgeBase)
    {
        _knowledgeBase = knowledgeBase;
    }

    public Task<SymptomExtraction> ExtractAsync(string userText, CancellationToken ct)
    {
        var present = _knowledgeBase
            .Where(entry => entry.Keywords.Any(k => userText.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .Select(entry => new ExtractedSymptom(entry.Id, Present: true))
            .ToList();

        return Task.FromResult(new SymptomExtraction(present, Provider: "keyword", Fallback: false));
    }
}
