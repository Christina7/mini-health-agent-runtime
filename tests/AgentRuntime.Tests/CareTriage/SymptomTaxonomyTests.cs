using CareTriageAgent;
using CareTriageAgent.Triage;

namespace AgentRuntime.Tests.CareTriage;

// Slice A: the symptom KB and red-flag rules are loaded from a versioned JSON taxonomy. These tests
// cover the loader's reference resolution + validation, and pin the #36 no-drift guarantee at the
// data level: the chest symptom's keywords and the cardiac red-flag chest group are the SAME phrase
// set by construction, not two lists that happen to agree.
public class SymptomTaxonomyTests
{
    [Fact]
    public void Embedded_taxonomy_loads_with_version_and_expected_shape()
    {
        var taxonomy = SymptomTaxonomy.Load();

        Assert.False(string.IsNullOrWhiteSpace(taxonomy.Version));
        Assert.Equal(8, taxonomy.KnowledgeBase.Count);
        Assert.Single(taxonomy.RedFlagRules);
        Assert.Equal(taxonomy.Version, CareTriageDomain.TaxonomyVersion);
    }

    // Structural no-drift (issue #36): chest_pain resolves its keywords from the same phrase set the
    // cardiac red-flag chest group resolves from, so the two are reference-identical — they cannot
    // drift apart because they are one list, not two copies.
    [Fact]
    public void Chest_symptom_and_cardiac_red_flag_share_the_same_phrase_set_instance()
    {
        var taxonomy = SymptomTaxonomy.Load();

        var chestKeywords = taxonomy.KnowledgeBase.Single(s => s.Id == "chest_pain").Keywords;
        var cardiac = taxonomy.RedFlagRules.Single(r => r.Id == "cardiac");
        var chestGroup = cardiac.AllOfAny
            .Single(group => group.Any(p => p.Contains("chest", StringComparison.OrdinalIgnoreCase)));

        Assert.Same(chestKeywords, chestGroup);
    }

    // The KB breathing entry and the red-flag breathing group are intentionally DIFFERENT lists today
    // (the KB knows "can't breathe"; the red flag knows "short of breath"/"difficulty breathing").
    // Lock that so the JSON move didn't silently merge them — that would be a behavior change.
    [Fact]
    public void Breathing_kb_entry_and_red_flag_group_stay_distinct()
    {
        var taxonomy = SymptomTaxonomy.Load();

        var kbBreathing = taxonomy.KnowledgeBase.Single(s => s.Id == "breathing_difficulty").Keywords;
        Assert.Contains("can't breathe", kbBreathing);
        Assert.DoesNotContain("short of breath", kbBreathing);
    }

    [Fact]
    public void Symptom_with_keywords_ref_resolves_to_the_named_phrase_set()
    {
        const string json = """
        {
          "taxonomyVersion": "test.1",
          "phraseSets": { "chest": ["chest pain", "chest pressure"] },
          "symptoms": [ { "id": "chest_pain", "keywordsRef": "chest", "baseSeverity": 7, "selfCareAdvice": "x" } ],
          "redFlags": [ { "id": "cardiac", "allOfAny": ["chest"], "message": "m" } ]
        }
        """;

        var taxonomy = SymptomTaxonomy.Parse(json);

        var entry = Assert.Single(taxonomy.KnowledgeBase);
        Assert.Equal(new[] { "chest pain", "chest pressure" }, entry.Keywords);
    }

    [Theory]
    [InlineData("""{ "taxonomyVersion": "t", "symptoms": [ { "id": "x", "keywordsRef": "missing", "baseSeverity": 1 } ] }""")]
    [InlineData("""{ "taxonomyVersion": "t", "phraseSets": { "a": ["x"] }, "redFlags": [ { "id": "r", "allOfAny": ["missing"], "message": "m" } ] }""")]
    [InlineData("""{ "taxonomyVersion": "t", "phraseSets": { "a": ["x"] }, "symptoms": [ { "id": "x", "keywords": ["y"], "keywordsRef": "a", "baseSeverity": 1 } ] }""")]
    [InlineData("""{ "taxonomyVersion": "t", "symptoms": [ { "id": "x", "baseSeverity": 1 } ] }""")]
    [InlineData("""{ "phraseSets": {} }""")]
    public void Malformed_taxonomy_throws_at_parse(string json)
    {
        Assert.Throws<InvalidOperationException>(() => SymptomTaxonomy.Parse(json));
    }
}
