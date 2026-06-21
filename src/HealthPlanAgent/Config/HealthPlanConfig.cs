namespace HealthPlanAgent.Config;

/// <summary>
/// The strongly-typed effective configuration for the plan app, deserialized from the runtime's
/// resolved config (base + flights). A sibling of the triage config — same shape, its own type, so the
/// two agents never share a config class. Defaults match the shipped runtimeconfig.json. Note the
/// <c>Plan</c> section tunes plan <i>generation</i> only; the safety thresholds live in the guardrail
/// as constants, so no flight can weaken them.
/// </summary>
public sealed record HealthPlanConfig
{
    public AgentConfig Agent { get; init; } = new();
    public Dictionary<string, ToolConfig> Tools { get; init; } = new();
    public PlanConfig Plan { get; init; } = new();
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
}

/// <summary>Plan-generation tuning. The deficit cap is what aggressive/conservative flights vary.</summary>
public sealed record PlanConfig
{
    public double DeficitCapFraction { get; init; } = 0.20;
    public double ProteinGramsPerKg { get; init; } = 1.8;
    public int CalorieFloor { get; init; } = 1200;
}

public sealed record ResilienceConfig
{
    public int ToolMaxRetries { get; init; } = 2;
}
