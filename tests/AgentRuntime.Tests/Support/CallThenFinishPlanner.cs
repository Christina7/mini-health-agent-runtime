using AgentRuntime.Context;
using AgentRuntime.Llm;

namespace AgentRuntime.Tests.Support;

/// <summary>
/// A two-step planner test double: calls a named tool on the first step, then finishes with a
/// fixed message on the second — without reading the observation (so it works even when the tool failed).
/// </summary>
public sealed class CallThenFinishPlanner : ILlmClient
{
    private readonly string _tool;
    private readonly string _finishMessage;
    private int _step;

    public CallThenFinishPlanner(string tool, string finishMessage)
    {
        _tool = tool;
        _finishMessage = finishMessage;
    }

    public Task<PlanDecision> PlanNextStepAsync(WorkContext ctx, IReadOnlyList<ToolDescriptor> tools, CancellationToken ct)
    {
        _step++;
        return Task.FromResult<PlanDecision>(_step == 1
            ? new PlanDecision.CallTool(_tool, default)
            : new PlanDecision.Finish(_finishMessage));
    }
}
