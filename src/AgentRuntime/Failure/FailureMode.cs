namespace AgentRuntime.Failure;

/// <summary>
/// How a failed operation is allowed to affect the user-facing response. Drives whether the
/// runtime degrades to a fallback or silently swallows the failure.
/// </summary>
public enum FailureMode
{
    /// <summary>Unclassified — treated conservatively as if it could impact the response.</summary>
    Unknown = 0,

    /// <summary>Failure matters: degrade the turn and substitute a safe fallback.</summary>
    CanImpactResponse,

    /// <summary>Failure is incidental: swallow it and continue without degrading.</summary>
    NeverImpactsResponse,
}
