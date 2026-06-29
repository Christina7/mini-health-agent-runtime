using System.Text.Json;
using AgentRuntime.Context;
using CareTriageAgent;
using CareTriageAgent.Guardrails;
using CareTriageAgent.Tools;
using CareTriageAgent.Triage;

namespace AgentRuntime.Tests.CareTriage;

// Issue #36: a cardiac chest phrase recognised by the red-flag guardrail must not be triaged below
// the level its synonyms receive. These tests run against the REAL production domain data
// (CareTriageDomain) so the regression is pinned to what the hosts actually ship.
public class CareTriageDomainTests
{
    // Mirrors the CareTriageConfig defaults: SelfCare <= 2, SeeGp <= 5, UrgentCare <= 8.
    private static readonly TriagePolicy Policy =
        new(new TriageThresholds(SelfCareMaxScore: 2, SeeGpMaxScore: 5, UrgentCareMaxScore: 8));

    // Criteria #1 + #2: every cardiac chest phrase, on its own, runs the real extract -> score
    // pipeline to a score of 7 and triages UrgentCare. Before the fix "chest pressure" was unknown to
    // the KB, scored 0, and fell to SelfCare — the unsafe direction — while "chest pain"/"chest
    // tightness" correctly reached UrgentCare. Drives the production extractor + scorer over the
    // production taxonomy, exactly as the planner wires them.
    [Theory]
    [InlineData("chest pain")]
    [InlineData("chest pressure")]
    [InlineData("chest tightness")]
    public async Task Cardiac_chest_phrase_alone_triages_urgent_care(string phrase)
    {
        var kb = CareTriageDomain.DefaultKnowledgeBase();
        var extraction = await new KeywordSymptomExtractor(kb).ExtractAsync($"I have {phrase} right now", CancellationToken.None);
        var presentIds = extraction.Symptoms.Where(s => s.Present).Select(s => s.Id).ToArray();

        var args = JsonSerializer.SerializeToElement(new { presentIds });
        var result = await new SymptomKnowledgeBaseTool(kb).ExecuteAsync(args, new WorkContext("c1"), CancellationToken.None);
        var score = result.Output.GetProperty("score").GetInt32();

        Assert.Equal(7, score);
        Assert.Equal(UrgencyLevel.UrgentCare, Policy.Classify(score));
    }

    // Criterion #4 (drift guard): the scoring layer (KB) must recognise every chest phrase the safety
    // layer (cardiac red-flag rule) treats as cardiac, so the two cannot silently drift apart again.
    [Fact]
    public void Kb_chest_keywords_cover_every_cardiac_red_flag_chest_phrase()
    {
        var kbChest = CareTriageDomain.DefaultKnowledgeBase()
            .Single(entry => entry.Id == "chest_pain").Keywords;

        var cardiac = CareTriageDomain.DefaultRedFlagRules().Single(rule => rule.Id == "cardiac");
        var chestGroup = cardiac.AllOfAny
            .Single(group => group.Any(phrase => phrase.Contains("chest", StringComparison.OrdinalIgnoreCase)));

        Assert.Subset(new HashSet<string>(kbChest), new HashSet<string>(chestGroup));
    }
}
