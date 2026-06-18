using System.Text.Json;
using System.Text.Json.Serialization;

namespace CareTriageAgent.Triage;

/// <summary>
/// The structured outcome of a triage turn. Carried back from the planner as JSON (so the same
/// contract works for the mock planner and a future LLM), then materialized by consumers via
/// <see cref="FromJson"/>. The disclaimer is always present.
/// </summary>
public sealed record TriageResult
{
    public UrgencyLevel Urgency { get; init; }
    public string RecommendedAction { get; init; } = string.Empty;
    public string Advice { get; init; } = string.Empty;
    public IReadOnlyList<string> ToolsInvoked { get; init; } = Array.Empty<string>();
    public bool RedFlagTriggered { get; init; }
    public bool Degraded { get; init; }
    public string Disclaimer { get; init; } = "Educational only — not medical advice.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public JsonElement ToJson() => JsonSerializer.SerializeToElement(this, JsonOptions);

    public static TriageResult FromJson(JsonElement element) =>
        element.Deserialize<TriageResult>(JsonOptions)
        ?? throw new JsonException("Could not deserialize TriageResult.");
}
