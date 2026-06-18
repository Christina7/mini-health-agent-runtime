using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;

namespace AgentRuntime.Config;

/// <summary>
/// Produces the effective runtime configuration by starting from a base JSON document and applying
/// an ordered set of named JSON-Patch "flight" overlays. Flights are allow-listed by name — callers
/// may only activate flights that were registered, never post arbitrary patch paths. This is the
/// config-driven execution model: behavior changes by toggling flights, with no recompile.
/// </summary>
public sealed class RuntimeConfigProvider
{
    private readonly string _baseJson;
    private readonly IReadOnlyDictionary<string, string> _flights;

    public RuntimeConfigProvider(string baseJson, IReadOnlyDictionary<string, string>? flights = null)
    {
        _baseJson = baseJson;
        _flights = flights ?? new Dictionary<string, string>();
    }

    private static readonly JsonSerializerOptions DeserializeOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Resolve the effective config and deserialize it into a strongly-typed config object.</summary>
    public T Resolve<T>(params string[] activeFlights)
    {
        var node = Resolve(activeFlights);
        return node.Deserialize<T>(DeserializeOptions)
            ?? throw new InvalidOperationException($"Effective config could not be read as {typeof(T).Name}.");
    }

    /// <summary>Resolve the effective config with the given flights applied, in order.</summary>
    public JsonNode Resolve(params string[] activeFlights)
    {
        var node = JsonNode.Parse(_baseJson)
            ?? throw new InvalidOperationException("Base configuration is not valid JSON.");

        foreach (var name in activeFlights)
        {
            if (!_flights.TryGetValue(name, out var patchJson))
            {
                throw new ArgumentException(
                    $"Unknown flight '{name}'. Flights are allow-listed; only registered flights can be applied.",
                    nameof(activeFlights));
            }

            var patch = JsonSerializer.Deserialize<JsonPatch>(patchJson)
                ?? throw new InvalidOperationException($"Flight '{name}' is not a valid JSON Patch.");

            var result = patch.Apply(node);
            if (result.Error is not null)
            {
                throw new InvalidOperationException($"Flight '{name}' could not be applied: {result.Error}");
            }

            node = result.Result
                ?? throw new InvalidOperationException($"Flight '{name}' produced no configuration.");
        }

        return node;
    }
}
