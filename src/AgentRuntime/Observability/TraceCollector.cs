using System.Collections.Concurrent;
using System.Diagnostics;

namespace AgentRuntime.Observability;

/// <summary>
/// Captures the runtime's spans in-process and reconstructs a single turn's <see cref="TraceNode"/>
/// tree. Registers an <see cref="ActivityListener"/> for the runtime's ActivitySource; spans are
/// bucketed by trace id so concurrent turns never cross-contaminate. Dispose after the turn.
/// </summary>
public sealed class TraceCollector : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly ConcurrentBag<Activity> _stopped = new();

    public TraceCollector()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RuntimeActivitySource.Name,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _stopped.Add(activity),
        };

        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>Build the trace tree for the given trace id from the spans captured so far.</summary>
    public TraceNode? BuildTree(ActivityTraceId traceId)
    {
        var spans = _stopped.Where(a => a.TraceId == traceId).ToList();
        if (spans.Count == 0)
        {
            return null;
        }

        var ids = spans.Select(a => a.Id).ToHashSet();
        var root = spans.FirstOrDefault(a => a.ParentId is null || !ids.Contains(a.ParentId));

        return root is null ? null : Build(root, spans);
    }

    private static TraceNode Build(Activity activity, List<Activity> all)
    {
        var children = all
            .Where(c => c.ParentId == activity.Id)
            .OrderBy(c => c.StartTimeUtc)
            .Select(c => Build(c, all))
            .ToList();

        var degraded = activity.GetTagItem("degraded") is true;
        return new TraceNode(activity.OperationName, activity.Duration.TotalMilliseconds, degraded, children);
    }

    public void Dispose() => _listener.Dispose();
}
