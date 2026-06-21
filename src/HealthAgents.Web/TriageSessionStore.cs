using System.Collections.Concurrent;
using CareTriageAgent;

namespace HealthAgents.Web;

/// <summary>
/// Holds one <see cref="CareTriageSession"/> per <c>conversationId</c> so multi-turn memory and
/// cross-turn cacheability are exercised across separate HTTP requests. In-memory and single-process
/// — a deliberate demo simplification (no DB); a multi-instance deployment would swap this for a
/// distributed store. Idle conversations are evicted after <see cref="_idleTtl"/>.
/// </summary>
public sealed class TriageSessionStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly TimeSpan _idleTtl;

    public TriageSessionStore(TimeSpan? idleTtl = null) =>
        _idleTtl = idleTtl ?? TimeSpan.FromMinutes(30);

    /// <summary>
    /// Returns the existing session for the conversation, or builds one with <paramref name="factory"/>
    /// on first contact. The config (flights / broken-tool toggle) is therefore fixed when the
    /// conversation is created — the first request for an id wins — which keeps a multi-turn session
    /// internally consistent.
    /// </summary>
    public CareTriageSession GetOrCreate(string conversationId, Func<CareTriageSession> factory)
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

    private sealed record Entry(CareTriageSession Session, DateTimeOffset LastSeenUtc);
}
