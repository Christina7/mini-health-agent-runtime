using System.Collections.Concurrent;
using HealthPlanAgent;

namespace HealthAgents.Web;

/// <summary>
/// Holds one <see cref="HealthPlanSession"/> per <c>conversationId</c> so the living plan (and its
/// growing progress) survives across separate HTTP requests — the plan-side mirror of
/// <c>TriageSessionStore</c>. In-memory and single-process, a deliberate demo simplification; a
/// multi-instance deployment would swap this for a distributed store. Idle conversations are evicted
/// after <see cref="_idleTtl"/>.
/// </summary>
public sealed class PlanSessionStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly TimeSpan _idleTtl;

    public PlanSessionStore(TimeSpan? idleTtl = null) =>
        _idleTtl = idleTtl ?? TimeSpan.FromMinutes(30);

    /// <summary>
    /// Returns the existing session for the conversation, or builds one with <paramref name="factory"/>
    /// on first contact. The config (flights) is therefore fixed when the conversation is created —
    /// the first request for an id wins — which keeps a multi-turn session internally consistent.
    /// </summary>
    public HealthPlanSession GetOrCreate(string conversationId, Func<HealthPlanSession> factory)
    {
        EvictExpired();
        var entry = _entries.AddOrUpdate(
            conversationId,
            _ => new Entry(factory(), DateTimeOffset.UtcNow),
            (_, existing) => existing with { LastSeenUtc = DateTimeOffset.UtcNow });
        return entry.Session;
    }

    private void EvictExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - _idleTtl;
        foreach (var kvp in _entries)
        {
            if (kvp.Value.LastSeenUtc < cutoff)
            {
                _entries.TryRemove(kvp.Key, out _);
            }
        }
    }

    private sealed record Entry(HealthPlanSession Session, DateTimeOffset LastSeenUtc);
}
