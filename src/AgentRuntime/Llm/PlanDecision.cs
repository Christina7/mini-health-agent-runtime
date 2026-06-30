using System.Text.Json;

namespace AgentRuntime.Llm;

/// <summary>
/// The next step the planner has decided on. The runtime owns the loop; a planner only
/// chooses the next step, never executes it. A closed hierarchy (private constructor) so
/// the orchestrator can switch exhaustively. More cases (AskUser) arrive in later slices.
/// </summary>
public abstract record PlanDecision
{
    private PlanDecision() { }

    /// <summary>Run the named tool with <paramref name="Args"/>, then observe and plan again.</summary>
    public sealed record CallTool(string ToolName, JsonElement Args) : PlanDecision;

    /// <summary>
    /// End the turn and reply with <paramref name="Message"/>. <paramref name="Result"/> is an
    /// optional structured payload (JSON) the app deserializes into its own result type.
    /// <paramref name="Summary"/> is an optional one-line trace label for the finishing step
    /// (e.g. the final urgency) — observability only, never user-facing.
    /// </summary>
    public sealed record Finish(string Message, JsonElement? Result = null, string? Summary = null) : PlanDecision;
}
