namespace CareTriageAgent.Config;

/// <summary>
/// The strongly-typed effective configuration for the triage app, deserialized from the runtime's
/// resolved config (base + flights). Defaults match the shipped runtimeconfig.json so a partial or
/// missing section still yields sensible values.
/// </summary>
public sealed record CareTriageConfig
{
    public AgentConfig Agent { get; init; } = new();
    public Dictionary<string, ToolConfig> Tools { get; init; } = new();
    public TriageConfig Triage { get; init; } = new();
    public ResilienceConfig Resilience { get; init; } = new();

    /// <summary>True if a tool with the given name is present and enabled.</summary>
    public bool IsToolEnabled(string name) => Tools.TryGetValue(name, out var t) && t.Enabled;
}

public sealed record AgentConfig
{
    public int MaxSteps { get; init; } = 6;
    public string LlmProvider { get; init; } = "mock";
}

public sealed record ToolConfig
{
    public bool Enabled { get; init; } = true;
    public string? DataFile { get; init; }
}

public sealed record TriageConfig
{
    public int SelfCareMaxScore { get; init; } = 2;
    public int SeeGpMaxScore { get; init; } = 5;
    public int UrgentCareMaxScore { get; init; } = 8;
}

public sealed record ResilienceConfig
{
    public int ToolMaxRetries { get; init; } = 2;
}
