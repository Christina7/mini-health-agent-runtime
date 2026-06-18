using CareTriageAgent.Guardrails;
using CareTriageAgent.Tools;

namespace CareTriageAgent;

/// <summary>
/// The single source of the health domain data — the symptom knowledge base and the red-flag rules.
/// Both hosts (CLI and Web) build their <see cref="CareTriageSession"/> from these factories so the
/// two surfaces stay in lock-step instead of each carrying its own copy of the data.
/// </summary>
public static class CareTriageDomain
{
    /// <summary>The synthetic symptom knowledge base scored by <see cref="SymptomKnowledgeBaseTool"/>.</summary>
    public static IReadOnlyList<SymptomEntry> DefaultKnowledgeBase() => new[]
    {
        new SymptomEntry("sore_throat", new[] { "sore throat", "throat pain" }, BaseSeverity: 1, SelfCareAdvice: "Rest, fluids, and throat lozenges usually help."),
        new SymptomEntry("fever", new[] { "fever", "high temperature" }, BaseSeverity: 1, SelfCareAdvice: "Stay hydrated and monitor your temperature."),
        new SymptomEntry("headache", new[] { "headache" }, BaseSeverity: 1, SelfCareAdvice: "Rest and over-the-counter pain relief may help."),
        new SymptomEntry("cough", new[] { "cough" }, BaseSeverity: 1, SelfCareAdvice: "A cough often clears on its own within a couple of weeks."),
        new SymptomEntry("dizziness", new[] { "dizzy", "dizziness", "lightheaded" }, BaseSeverity: 3, SelfCareAdvice: "Sit or lie down; avoid sudden movements."),
        new SymptomEntry("abdominal_pain", new[] { "abdominal pain", "stomach pain", "belly pain" }, BaseSeverity: 4, SelfCareAdvice: "Note where the pain is and whether it worsens."),
        new SymptomEntry("chest_pain", new[] { "chest pain", "chest tightness" }, BaseSeverity: 7, SelfCareAdvice: "Chest pain should be assessed promptly."),
        new SymptomEntry("breathing_difficulty", new[] { "shortness of breath", "trouble breathing", "can't breathe" }, BaseSeverity: 8, SelfCareAdvice: "Difficulty breathing needs prompt assessment."),
    };

    /// <summary>The red-flag rules the always-on guardrail escalates on (never config-disableable).</summary>
    public static IReadOnlyList<RedFlagRule> DefaultRedFlagRules() => new[]
    {
        new RedFlagRule(
            Id: "cardiac",
            AllOf: new[] { "chest pain", "shortness of breath" },
            Message: "🚨 Possible cardiac emergency — call your local emergency number / go to the ER now."),
    };
}
