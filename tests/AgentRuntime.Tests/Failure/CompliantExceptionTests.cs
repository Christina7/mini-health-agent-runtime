using AgentRuntime.Failure;

namespace AgentRuntime.Tests.Failure;

public class CompliantExceptionTests
{
    // Slice 7: a CompliantException keeps the internal detail (for logs/traces) separate from
    // the user-safe message, so sensitive internals never leak into a user-facing reply.
    [Fact]
    public void Separates_internal_detail_from_user_safe_message()
    {
        var ex = new CompliantException(
            internalMessage: "clinic_finder failed: connstring=secret-xyz unreachable",
            userSafeMessage: "I couldn't verify nearby clinics right now.",
            failureMode: FailureMode.CanImpactResponse);

        Assert.Equal("I couldn't verify nearby clinics right now.", ex.UserSafeMessage);
        Assert.Equal(FailureMode.CanImpactResponse, ex.FailureMode);

        // Internal detail lives only in the base Message (logs/trace), never in the user-safe text.
        Assert.Contains("connstring=secret-xyz", ex.Message);
        Assert.DoesNotContain("secret-xyz", ex.UserSafeMessage);
    }
}
