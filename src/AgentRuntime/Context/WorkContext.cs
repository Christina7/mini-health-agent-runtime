using AgentRuntime.Tools;

namespace AgentRuntime.Context;

/// <summary>
/// Holds cross-turn conversation state for a single agent session. Domain-agnostic:
/// the runtime never puts health-specific types here (the app uses the typed state bag,
/// added in a later slice). Keyed by <see cref="ConversationId"/> by the host.
/// </summary>
public sealed class WorkContext
{
    private readonly List<Turn> _history = new();
    private readonly List<Observation> _observations = new();

    public WorkContext(string conversationId)
    {
        ConversationId = conversationId;
    }

    public string ConversationId { get; }

    /// <summary>True once a tool failure forced the turn into a degraded (safe-fallback) mode.</summary>
    public bool Degraded { get; internal set; }

    /// <summary>User and agent messages, in order, across every turn of this conversation.</summary>
    public IReadOnlyList<Turn> History => _history;

    /// <summary>Tool results gathered during the current turn's act -> observe loop.</summary>
    public IReadOnlyList<Observation> Observations => _observations;

    /// <summary>The most recent user message, or empty string if none yet.</summary>
    public string LatestUserText
    {
        get
        {
            for (var i = _history.Count - 1; i >= 0; i--)
            {
                if (_history[i].Role == TurnRole.User)
                {
                    return _history[i].Text;
                }
            }

            return string.Empty;
        }
    }

    /// <summary>
    /// Resets the per-turn working state at the start of a new turn: the observations gathered during
    /// the act -> observe loop and the degraded flag. Cross-turn memory (<see cref="History"/>) is
    /// preserved, so a follow-up is triaged against its own symptoms, not the previous turn's.
    /// </summary>
    internal void BeginTurn()
    {
        _observations.Clear();
        Degraded = false;
    }

    public void AppendUser(string text) => _history.Add(new Turn(TurnRole.User, text));

    public void AppendAgent(string text) => _history.Add(new Turn(TurnRole.Agent, text));

    public void RecordObservation(string toolName, ToolResult result) =>
        _observations.Add(new Observation(toolName, result));
}

/// <summary>One tool invocation's result, recorded so the planner can read it on the next step.</summary>
public sealed record Observation(string ToolName, ToolResult Result);

public enum TurnRole
{
    User,
    Agent
}

public sealed record Turn(TurnRole Role, string Text);
