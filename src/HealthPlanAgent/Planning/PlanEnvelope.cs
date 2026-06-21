using System.Text.Json;
using System.Text.Json.Serialization;

namespace HealthPlanAgent.Planning;

/// <summary>Which kind of turn the user is taking.</summary>
public enum PlanAction
{
    Create,
    Log
}

/// <summary>One day's logged input on a <c>Log</c> turn: calories eaten and tasks ticked off.</summary>
public sealed record DayLog(int CaloriesLogged, int TasksCompleted);

/// <summary>
/// The typed turn input. It travels out-of-band (not through the user message, which stays
/// human-readable for the trace) and is read by the guardrail and the planner via a
/// <see cref="TurnInputHolder"/>. A <c>Create</c> turn carries the goal + profile; a <c>Log</c> turn
/// carries the day's <see cref="DayLog"/> (the prior plan is supplied separately as
/// <see cref="TurnInputHolder.PriorArtifact"/>, so the request never has to resend it).
/// </summary>
public sealed record PlanEnvelope(
    PlanAction Action,
    HealthGoal? Goal = null,
    HealthProfile? Profile = null,
    DayLog? Log = null);

/// <summary>
/// A session-scoped slot the host fills before each turn, shared by the guardrail and the planner so
/// they read the <b>same</b> input without it ever touching the domain-agnostic WorkContext. The
/// session also threads its running plan back through <see cref="PriorArtifact"/> so a log turn can
/// evaluate the day against the existing targets and append to the existing progress.
/// </summary>
public sealed class TurnInputHolder
{
    public PlanEnvelope? Current { get; set; }

    public HealthPlanResult? PriorArtifact { get; set; }
}

/// <summary>The one shared serializer so tools, planner, and host can't drift on JSON shape.</summary>
internal static class PlanJson
{
    public static readonly JsonSerializerOptions Options =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
}
