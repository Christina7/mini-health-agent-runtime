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

    /// <summary>End the turn and reply to the user with <paramref name="Message"/>.</summary>
    public sealed record Finish(string Message) : PlanDecision;
}
