using System.Text.Json;
using System.Text.Json.Serialization;

namespace HealthPlanAgent.Planning;

/// <summary>Which kind of turn the user is taking.</summary>
public enum PlanAction
{
    Create,
    Log
}

/// <summary>
/// The typed turn input. It travels out-of-band (not through the user message, which stays
/// human-readable for the trace) and is read by the guardrail and the planner via a
/// <see cref="TurnInputHolder"/>. <c>plan</c>/<c>progress</c> for log turns arrive in a later slice.
/// </summary>
public sealed record PlanEnvelope(
    PlanAction Action,
    HealthGoal? Goal = null,
    HealthProfile? Profile = null);

/// <summary>
/// A session-scoped slot the host fills before each turn, shared by the guardrail and the planner so
/// they read the <b>same</b> envelope without it ever touching the domain-agnostic WorkContext.
/// </summary>
public sealed class TurnInputHolder
{
    public PlanEnvelope? Current { get; set; }
}

/// <summary>The one shared serializer so tools, planner, and host can't drift on JSON shape.</summary>
internal static class PlanJson
{
    public static readonly JsonSerializerOptions Options =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
}
