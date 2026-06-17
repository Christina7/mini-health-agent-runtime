using AgentRuntime.Context;
using AgentRuntime.Llm;

namespace AgentRuntime.Tests.Support;

/// <summary>
/// A two-step planner test double: on the first step it calls a named tool; on the second
/// step it reads the most recent observation, pulls a string field out of the tool's JSON
/// output, and finishes with it. This exercises the orchestrator's act -> observe -> plan loop.
/// </summary>
public sealed class ObserveThenFinishPlanner : ILlmClient
{
    private readonly string _toolToCall;
    private readonly string _adviceKey;
    private int _step;

    public ObserveThenFinishPlanner(string toolToCall, string adviceKey)
    {
        _toolToCall = toolToCall;
        _adviceKey = adviceKey;
    }

    public Task<PlanDecision> PlanNextStepAsync(
        WorkContext ctx, IReadOnlyList<ToolDescriptor> tools, CancellationToken ct)
    {
        _step++;

        if (_step == 1)
        {
            return Task.FromResult<PlanDecision>(new PlanDecision.CallTool(_toolToCall, default));
        }

        var observation = ctx.Observations[^1];
        var advice = observation.Result.Output.GetProperty(_adviceKey).GetString();
        return Task.FromResult<PlanDecision>(new PlanDecision.Finish($"Recommendation: {advice}."));
    }
}
