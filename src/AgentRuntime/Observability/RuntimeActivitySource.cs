using System.Diagnostics;

namespace AgentRuntime.Observability;

/// <summary>
/// The single <see cref="ActivitySource"/> the runtime emits spans from. ActivitySource is the
/// OpenTelemetry instrumentation API in .NET, so these spans flow to any OTLP/console exporter the
/// host wires up — and are also captured in-process by <see cref="TraceCollector"/> for the trace tree.
/// </summary>
public static class RuntimeActivitySource
{
    public const string Name = "AgentRuntime";

    public static readonly ActivitySource Source = new(Name);
}
