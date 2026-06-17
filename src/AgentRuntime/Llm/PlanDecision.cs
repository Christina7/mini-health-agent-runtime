namespace AgentRuntime.Llm;

/// <summary>
/// The next step the planner has decided on. The runtime owns the loop; a planner only
/// chooses the next step, never executes it. A closed hierarchy (private constructor) so
/// the orchestrator can switch exhaustively. More cases (CallTool, AskUser) arrive in later slices.
/// </summary>
public abstract record PlanDecision
{
    private PlanDecision() { }

    /// <summary>End the turn and reply to the user with <paramref name="Message"/>.</summary>
    public sealed record Finish(string Message) : PlanDecision;
}
