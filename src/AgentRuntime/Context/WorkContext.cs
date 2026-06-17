namespace AgentRuntime.Context;

/// <summary>
/// Holds cross-turn conversation state for a single agent session. Domain-agnostic:
/// the runtime never puts health-specific types here (the app uses the typed state bag,
/// added in a later slice). Keyed by <see cref="ConversationId"/> by the host.
/// </summary>
public sealed class WorkContext
{
    private readonly List<Turn> _history = new();

    public WorkContext(string conversationId)
    {
        ConversationId = conversationId;
    }

    public string ConversationId { get; }

    /// <summary>User and agent messages, in order, across every turn of this conversation.</summary>
    public IReadOnlyList<Turn> History => _history;

    public void AppendUser(string text) => _history.Add(new Turn(TurnRole.User, text));

    public void AppendAgent(string text) => _history.Add(new Turn(TurnRole.Agent, text));
}

public enum TurnRole
{
    User,
    Agent
}

public sealed record Turn(TurnRole Role, string Text);
