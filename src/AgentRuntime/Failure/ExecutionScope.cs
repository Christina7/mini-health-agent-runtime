namespace AgentRuntime.Failure;

/// <summary>The result of running an operation through an <see cref="ExecutionScope"/>.</summary>
/// <param name="Value">The operation's value, the fallback, or default if it failed with no fallback.</param>
/// <param name="Degraded">True if a terminal failure forced a degraded (fallback) outcome.</param>
/// <param name="Failed">True if the operation ultimately failed (after retries).</param>
public sealed record ScopeResult<T>(T? Value, bool Degraded, bool Failed);

/// <summary>
/// Runs an operation with a unified resilience policy: try, retry up to a configured count, then
/// on terminal failure either degrade to a fallback (<see cref="FailureMode.CanImpactResponse"/>)
/// or swallow it (<see cref="FailureMode.NeverImpactsResponse"/>). It never throws the operation's
/// exception to the caller — failure is reported through <see cref="ScopeResult{T}"/>.
/// </summary>
public sealed class ExecutionScope
{
    private readonly int _maxRetries;

    public ExecutionScope(int maxRetries)
    {
        _maxRetries = maxRetries;
    }

    public async Task<ScopeResult<T>> TryExecuteAsync<T>(
        string operation,
        FailureMode mode,
        Func<CancellationToken, Task<T>> action,
        Func<T>? fallback,
        CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var value = await action(ct);
                return new ScopeResult<T>(value, Degraded: false, Failed: false);
            }
            catch when (attempt < _maxRetries)
            {
                // Retry budget remains: try again.
            }
            catch
            {
                // Terminal failure: react according to the declared failure mode.
                return mode == FailureMode.NeverImpactsResponse
                    ? new ScopeResult<T>(default, Degraded: false, Failed: true)
                    : new ScopeResult<T>(fallback is null ? default : fallback(), Degraded: true, Failed: true);
            }
        }
    }
}
