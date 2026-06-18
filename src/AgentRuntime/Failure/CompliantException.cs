namespace AgentRuntime.Failure;

/// <summary>
/// An exception that carries a user-safe message separately from its internal detail. The base
/// <see cref="Exception.Message"/> holds the diagnostic detail (logs/traces only); only
/// <see cref="UserSafeMessage"/> is ever shown to a user. This keeps sensitive internals out of
/// user-facing replies even when something fails.
/// </summary>
public sealed class CompliantException : Exception
{
    public CompliantException(string internalMessage, string userSafeMessage, FailureMode failureMode)
        : base(internalMessage)
    {
        UserSafeMessage = userSafeMessage;
        FailureMode = failureMode;
    }

    /// <summary>The only text ever safe to show a user.</summary>
    public string UserSafeMessage { get; }

    public FailureMode FailureMode { get; }
}
