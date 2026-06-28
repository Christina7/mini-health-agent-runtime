using CareTriageAgent.Guardrails;
using CareTriageAgent.Tools;
using CareTriageAgent.Triage;

namespace CareTriageAgent;

/// <summary>
/// The single source of the health domain data — the symptom knowledge base and the red-flag rules.
/// Both hosts (CLI and Web) build their <see cref="CareTriageSession"/> from these factories so the
/// two surfaces stay in lock-step instead of each carrying its own copy of the data.
///
/// The data itself lives in a versioned JSON taxonomy (<c>triage-taxonomy.json</c>) loaded once via
/// <see cref="SymptomTaxonomy"/>; these factories just expose its scoring and safety views. Phrase
/// sets there are shared by reference, so the KB's chest entry and the cardiac red-flag rule are the
/// same list and cannot drift apart — exactly how "chest pressure" alone fell to SelfCare (issue #36).
/// </summary>
public static class CareTriageDomain
{
    private static readonly SymptomTaxonomy Taxonomy = SymptomTaxonomy.Load();

    /// <summary>The version stamp of the loaded taxonomy (recorded in audit records in a later slice).</summary>
    public static string TaxonomyVersion => Taxonomy.Version;

    /// <summary>The synthetic symptom knowledge base scored by <see cref="SymptomKnowledgeBaseTool"/>.</summary>
    public static IReadOnlyList<SymptomEntry> DefaultKnowledgeBase() => Taxonomy.KnowledgeBase;

    /// <summary>The red-flag rules the always-on guardrail escalates on (never config-disableable).</summary>
    public static IReadOnlyList<RedFlagRule> DefaultRedFlagRules() => Taxonomy.RedFlagRules;
}
