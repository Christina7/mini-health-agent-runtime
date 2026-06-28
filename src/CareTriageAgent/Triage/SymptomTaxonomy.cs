using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using CareTriageAgent.Guardrails;
using CareTriageAgent.Tools;

namespace CareTriageAgent.Triage;

/// <summary>
/// The versioned health taxonomy, loaded from <c>triage-taxonomy.json</c> (an embedded resource so
/// both hosts read the exact same bytes). It is the single source that feeds the deterministic
/// scoring layer (<see cref="KnowledgeBase"/>) and the safety layer (<see cref="RedFlagRules"/>).
///
/// Phrase sets are named once and <b>referenced</b> by both symptoms (<c>keywordsRef</c>) and
/// red-flag groups (<c>allOfAny</c>), so a shared list — e.g. the cardiac chest phrases — is the
/// same data in both places and cannot drift apart again (issue #36). A dangling reference or a
/// malformed entry throws at load, failing fast at startup rather than silently mis-scoring.
/// </summary>
public sealed class SymptomTaxonomy
{
    public string Version { get; }
    public IReadOnlyList<SymptomEntry> KnowledgeBase { get; }
    public IReadOnlyList<RedFlagRule> RedFlagRules { get; }

    private SymptomTaxonomy(string version, IReadOnlyList<SymptomEntry> knowledgeBase, IReadOnlyList<RedFlagRule> redFlagRules)
    {
        Version = version;
        KnowledgeBase = knowledgeBase;
        RedFlagRules = redFlagRules;
    }

    private const string ResourceName = "CareTriageAgent.Triage.triage-taxonomy.json";

    /// <summary>Loads the taxonomy bundled with the assembly.</summary>
    public static SymptomTaxonomy Load()
    {
        var assembly = typeof(SymptomTaxonomy).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded taxonomy resource '{ResourceName}' not found.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Parses a taxonomy from JSON. Exposed for tests; resolves and validates all references.</summary>
    public static SymptomTaxonomy Parse(string json)
    {
        var dto = JsonSerializer.Deserialize<TaxonomyDto>(json, SerializerOptions)
            ?? throw new InvalidOperationException("Taxonomy JSON deserialized to null.");

        if (string.IsNullOrWhiteSpace(dto.TaxonomyVersion))
        {
            throw new InvalidOperationException("Taxonomy is missing 'taxonomyVersion'.");
        }

        var phraseSets = dto.PhraseSets ?? new Dictionary<string, List<string>>();

        IReadOnlyList<string> ResolveSet(string name, string context)
        {
            if (!phraseSets.TryGetValue(name, out var phrases))
            {
                throw new InvalidOperationException($"{context} references unknown phrase set '{name}'.");
            }
            return phrases;
        }

        var knowledgeBase = (dto.Symptoms ?? new List<SymptomDto>()).Select(symptom =>
        {
            if (string.IsNullOrWhiteSpace(symptom.Id))
            {
                throw new InvalidOperationException("A symptom is missing 'id'.");
            }

            var hasInline = symptom.Keywords is { Count: > 0 };
            var hasRef = !string.IsNullOrWhiteSpace(symptom.KeywordsRef);
            if (hasInline == hasRef)
            {
                throw new InvalidOperationException(
                    $"Symptom '{symptom.Id}' must have exactly one of 'keywords' or 'keywordsRef'.");
            }

            var keywords = hasRef
                ? ResolveSet(symptom.KeywordsRef!, $"Symptom '{symptom.Id}'")
                : symptom.Keywords!;

            return new SymptomEntry(symptom.Id, keywords, symptom.BaseSeverity, symptom.SelfCareAdvice ?? string.Empty);
        }).ToList();

        var redFlagRules = (dto.RedFlags ?? new List<RedFlagDto>()).Select(flag =>
        {
            if (string.IsNullOrWhiteSpace(flag.Id))
            {
                throw new InvalidOperationException("A red-flag rule is missing 'id'.");
            }
            if (flag.AllOfAny is not { Count: > 0 })
            {
                throw new InvalidOperationException($"Red-flag rule '{flag.Id}' must list at least one phrase set in 'allOfAny'.");
            }

            var groups = flag.AllOfAny
                .Select(name => ResolveSet(name, $"Red-flag rule '{flag.Id}'"))
                .ToArray();

            return new RedFlagRule(flag.Id, groups, flag.Message ?? string.Empty);
        }).ToList();

        return new SymptomTaxonomy(dto.TaxonomyVersion!, knowledgeBase, redFlagRules);
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class TaxonomyDto
    {
        [JsonPropertyName("taxonomyVersion")] public string? TaxonomyVersion { get; set; }
        [JsonPropertyName("phraseSets")] public Dictionary<string, List<string>>? PhraseSets { get; set; }
        [JsonPropertyName("symptoms")] public List<SymptomDto>? Symptoms { get; set; }
        [JsonPropertyName("redFlags")] public List<RedFlagDto>? RedFlags { get; set; }
    }

    private sealed class SymptomDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("keywords")] public List<string>? Keywords { get; set; }
        [JsonPropertyName("keywordsRef")] public string? KeywordsRef { get; set; }
        [JsonPropertyName("baseSeverity")] public int BaseSeverity { get; set; }
        [JsonPropertyName("selfCareAdvice")] public string? SelfCareAdvice { get; set; }
    }

    private sealed class RedFlagDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("allOfAny")] public List<string>? AllOfAny { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }
}
