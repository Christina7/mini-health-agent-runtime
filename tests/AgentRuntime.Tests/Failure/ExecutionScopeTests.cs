using AgentRuntime.Failure;

namespace AgentRuntime.Tests.Failure;

public class ExecutionScopeTests
{
    // Slice 7: a transient failure is retried and, if a later attempt succeeds, the result is
    // returned normally — not degraded.
    [Fact]
    public async Task Transient_failure_is_retried_then_succeeds()
    {
        var scope = new ExecutionScope(maxRetries: 2);
        var calls = 0;

        var result = await scope.TryExecuteAsync(
            "op",
            FailureMode.CanImpactResponse,
            ct =>
            {
                calls++;
                if (calls < 2) throw new InvalidOperationException("transient");
                return Task.FromResult("ok");
            },
            fallback: null,
            CancellationToken.None);

        Assert.False(result.Failed);
        Assert.False(result.Degraded);
        Assert.Equal("ok", result.Value);
        Assert.Equal(2, calls); // failed once, succeeded on the retry
    }

    // A terminal failure on an operation that can impact the response degrades the turn and
    // substitutes the fallback value (never throws to the caller).
    [Fact]
    public async Task Terminal_failure_that_can_impact_degrades_and_uses_fallback()
    {
        var scope = new ExecutionScope(maxRetries: 1);
        var calls = 0;

        var result = await scope.TryExecuteAsync<string>(
            "op",
            FailureMode.CanImpactResponse,
            ct => { calls++; throw new InvalidOperationException("boom"); },
            fallback: () => "safe-fallback",
            CancellationToken.None);

        Assert.True(result.Failed);
        Assert.True(result.Degraded);
        Assert.Equal("safe-fallback", result.Value);
        Assert.Equal(2, calls); // first attempt + one retry
    }

    // A terminal failure on an operation that never impacts the response is swallowed: the turn
    // continues and is NOT marked degraded.
    [Fact]
    public async Task Terminal_failure_that_never_impacts_is_swallowed_without_degrading()
    {
        var scope = new ExecutionScope(maxRetries: 0);

        var result = await scope.TryExecuteAsync<string>(
            "op",
            FailureMode.NeverImpactsResponse,
            ct => throw new InvalidOperationException("ignored"),
            fallback: null,
            CancellationToken.None);

        Assert.True(result.Failed);
        Assert.False(result.Degraded);
        Assert.Null(result.Value);
    }
}
