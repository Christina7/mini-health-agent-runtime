using System.Text.Json.Serialization;
using AgentRuntime.Config;
using AgentRuntime.Context;
using AgentRuntime.Failure;
using AgentRuntime.Observability;
using AgentRuntime.Orchestration;
using AgentRuntime.Tools;
using CareTriageAgent;
using CareTriageAgent.Config;
using CareTriageAgent.Tools;
using CareTriageAgent.Triage;
using CareTriageAgent.Web;

// Web host: a deliberately thin surface over the runtime. It owns no agent logic — it maps HTTP to
// the same CareTriageSession the CLI drives, keeps one session per conversationId so multi-turn
// memory is exercised across requests, and returns the reply plus the serialized trace tree for the
// browser to render. The runtime runs entirely server-side; no agent logic leaks into JavaScript.

var builder = WebApplication.CreateBuilder(args);

// Enum-as-string + camelCase so the JSON shape matches the documented HTTP contract.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var app = builder.Build();

// Load base config + allow-listed flights from disk (relative to the content root so it resolves
// both under `dotnet run` and the in-memory WebApplicationFactory test host).
var configDir = Path.Combine(app.Environment.ContentRootPath, "config");
var baseJson = File.ReadAllText(Path.Combine(configDir, "runtimeconfig.json"));
var flightsDir = Path.Combine(configDir, "flights");
var flights = Directory.Exists(flightsDir)
    ? Directory.GetFiles(flightsDir, "*.json").ToDictionary(f => Path.GetFileNameWithoutExtension(f)!, File.ReadAllText)
    : new Dictionary<string, string>();
var configProvider = new RuntimeConfigProvider(baseJson, flights);

var sessions = new TriageSessionStore();

// Build a fresh session for a conversation from its requested flights / broken-tool toggle. Flights
// are allow-listed by RuntimeConfigProvider (an unknown name throws ArgumentException) so the browser
// cannot post arbitrary config — and there is no guardrail toggle to begin with, keeping the safety
// invariant intact over HTTP.
CareTriageSession BuildSession(string conversationId, string[]? activeFlights, bool breakSymptomKb)
{
    var config = configProvider.Resolve<CareTriageConfig>(activeFlights ?? Array.Empty<string>());
    var tools = new ITool[]
    {
        breakSymptomKb
            ? new BrokenSymptomTool()
            : new SymptomKnowledgeBaseTool(CareTriageDomain.DefaultKnowledgeBase()),
    };
    return new CareTriageSession(config, tools, CareTriageDomain.DefaultRedFlagRules(), conversationId);
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/triage", async (TriageRequest request, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Message))
    {
        return Results.BadRequest(new { error = "A non-empty 'message' is required." });
    }

    // Client-generated id threads the conversation; absent ⇒ the server mints a new one.
    var conversationId = string.IsNullOrWhiteSpace(request.ConversationId)
        ? Guid.NewGuid().ToString("n")
        : request.ConversationId!;

    CareTriageSession session;
    try
    {
        session = sessions.GetOrCreate(
            conversationId,
            () => BuildSession(conversationId, request.Flights, request.BreakSymptomKb ?? false));
    }
    catch (ArgumentException ex)
    {
        // Unknown / disallowed flight name — a client error, not a server fault.
        return Results.BadRequest(new { error = ex.Message });
    }

    TurnResult turn;
    try
    {
        turn = await session.OnUserMessageAsync(request.Message, ct);
    }
    catch (CompliantException ex)
    {
        // A compliant failure maps to a 200 carrying only the user-safe message + degraded:true —
        // the HTTP analogue of a DegradedResponse. Internal detail never reaches the client.
        return Results.Ok(new TriageResponse(
            conversationId, ex.UserSafeMessage, Triage: null, Trace: null,
            Degraded: true, TurnCount: CountUserTurns(session)));
    }

    var triage = turn.Result is { } resultJson ? TriageResult.FromJson(resultJson) : null;
    return Results.Ok(new TriageResponse(
        conversationId, turn.Message, triage, turn.Trace, turn.Degraded, CountUserTurns(session)));
});

app.Run();

static int CountUserTurns(CareTriageSession session) =>
    session.Context.History.Count(t => t.Role == TurnRole.User);

// Merges with the compiler-generated top-level Program class so WebApplicationFactory<Program> can
// boot this host in-memory for the integration tests.
public partial class Program;

namespace CareTriageAgent.Web
{
    /// <summary>POST /triage request body. See DESIGN.md "HTTP contract".</summary>
    public sealed record TriageRequest(
        string? ConversationId,
        string Message,
        string[]? Flights = null,
        string? Provider = null,
        bool? BreakSymptomKb = null);

    /// <summary>
    /// POST /triage response. <see cref="Triage"/> is null until the turn reaches a Finish;
    /// <see cref="TurnCount"/> is the number of user messages remembered in this conversation, which
    /// makes the server-side multi-turn memory observable to the client (and the integration test).
    /// </summary>
    public sealed record TriageResponse(
        string ConversationId,
        string Reply,
        TriageResult? Triage,
        TraceNode? Trace,
        bool Degraded,
        int TurnCount);
}
