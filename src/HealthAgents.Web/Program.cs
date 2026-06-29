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
using HealthAgents.Web;
using HealthPlanAgent;
using HealthPlanAgent.Config;
using HealthPlanAgent.Planning;

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
// both under `dotnet run` and the in-memory WebApplicationFactory test host). Per-agent subfolder
// `config/triage/` keeps the triage config separate from the plan agent's (added in a later slice).
var configDir = Path.Combine(app.Environment.ContentRootPath, "config", "triage");
var baseJson = File.ReadAllText(Path.Combine(configDir, "runtimeconfig.json"));
var flightsDir = Path.Combine(configDir, "flights");
var flights = Directory.Exists(flightsDir)
    ? Directory.GetFiles(flightsDir, "*.json").ToDictionary(f => Path.GetFileNameWithoutExtension(f)!, File.ReadAllText)
    : new Dictionary<string, string>();
var configProvider = new RuntimeConfigProvider(baseJson, flights);

var sessions = new TriageSessionStore();

// Plan agent: its own config subfolder and session store, driving the SAME runtime as triage — the
// proof that one engine hosts two very different agents. Flights (aggressive/conservative) are
// allow-listed from config/plan/flights, exactly like triage's.
var planConfigDir = Path.Combine(app.Environment.ContentRootPath, "config", "plan");
var planFlightsDir = Path.Combine(planConfigDir, "flights");
var planFlights = Directory.Exists(planFlightsDir)
    ? Directory.GetFiles(planFlightsDir, "*.json").ToDictionary(f => Path.GetFileNameWithoutExtension(f)!, File.ReadAllText)
    : new Dictionary<string, string>();
var planConfigProvider = new RuntimeConfigProvider(
    File.ReadAllText(Path.Combine(planConfigDir, "runtimeconfig.json")), planFlights);
var planSessions = new PlanSessionStore();

HealthPlanSession BuildPlanSession(string conversationId, string[]? activeFlights, bool breakPlanGenerator) =>
    new(planConfigProvider.Resolve<HealthPlanConfig>(activeFlights ?? Array.Empty<string>()), conversationId, breakPlanGenerator);

// Build a fresh session for a conversation from its requested flights / broken-tool toggle. Flights
// are allow-listed by RuntimeConfigProvider (an unknown name throws ArgumentException) so the browser
// cannot post arbitrary config — and there is no guardrail toggle to begin with, keeping the safety
// invariant intact over HTTP.
CareTriageSession BuildSession(string conversationId, string[]? activeFlights, bool breakSymptomKb)
{
    var config = configProvider.Resolve<CareTriageConfig>(activeFlights ?? Array.Empty<string>());
    var kb = CareTriageDomain.DefaultKnowledgeBase();
    var tools = new ITool[]
    {
        new SymptomExtractorTool(new KeywordSymptomExtractor(kb)),
        breakSymptomKb
            ? new BrokenSymptomTool()
            : new SymptomKnowledgeBaseTool(kb),
    };
    return new CareTriageSession(config, tools, CareTriageDomain.DefaultRedFlagRules(), conversationId);
}

app.UseStaticFiles();

// The guided walkthrough is the landing page; the live chat app is one click away at /app. (Both
// files are also reachable by their own names, e.g. /walkthrough.html, via the static middleware.)
var webRoot = app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
app.MapGet("/", () => Results.File(Path.Combine(webRoot, "walkthrough.html"), "text/html"));
app.MapGet("/app", () => Results.File(Path.Combine(webRoot, "index.html"), "text/html"));
app.MapGet("/plan-app", () => Results.File(Path.Combine(webRoot, "planner.html"), "text/html"));

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

app.MapPost("/plan", async (PlanRequest request, CancellationToken ct) =>
{
    // The host validates shape; the guardrail (server-side) owns safety. A create needs a goal +
    // profile; a log needs the day's numbers. The prior plan is held server-side, not resent.
    if (request.Action == PlanAction.Create && (request.Goal is null || request.Profile is null))
    {
        return Results.BadRequest(new { error = "A create request needs a goal and a profile." });
    }

    if (request.Action == PlanAction.Log && request.Log is null)
    {
        return Results.BadRequest(new { error = "A log request needs the day's log." });
    }

    var conversationId = string.IsNullOrWhiteSpace(request.ConversationId)
        ? Guid.NewGuid().ToString("n")
        : request.ConversationId!;

    HealthPlanSession session;
    try
    {
        session = planSessions.GetOrCreate(
            conversationId, () => BuildPlanSession(conversationId, request.Flights, request.BreakPlanGenerator ?? false));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    var envelope = new PlanEnvelope(request.Action, request.Goal, request.Profile, request.Log);

    TurnResult turn;
    try
    {
        turn = await session.SubmitAsync(envelope, ct);
    }
    catch (CompliantException ex)
    {
        return Results.Ok(new PlanResponse(conversationId, ex.UserSafeMessage, Plan: null, Trace: null, Degraded: true));
    }

    var plan = turn.Result is { } json ? HealthPlanResult.FromJson(json) : null;
    return Results.Ok(new PlanResponse(conversationId, turn.Message, plan, turn.Trace, turn.Degraded));
});

app.Run();

static int CountUserTurns(CareTriageSession session) =>
    session.Context.History.Count(t => t.Role == TurnRole.User);

// Merges with the compiler-generated top-level Program class so WebApplicationFactory<Program> can
// boot this host in-memory for the integration tests.
public partial class Program;

namespace HealthAgents.Web
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

    /// <summary>
    /// POST /plan request body. <see cref="Action"/> selects create vs log; a create carries the goal
    /// and profile, a log carries only the day's <see cref="Log"/> (the prior plan is held server-side).
    /// See DESIGN.md "HTTP contract".
    /// </summary>
    public sealed record PlanRequest(
        string? ConversationId,
        PlanAction Action,
        HealthGoal? Goal = null,
        HealthProfile? Profile = null,
        DayLog? Log = null,
        string[]? Flights = null,
        bool? BreakPlanGenerator = null);

    /// <summary>POST /plan response. <see cref="Plan"/> is null when the guardrail short-circuits.</summary>
    public sealed record PlanResponse(
        string ConversationId,
        string Reply,
        HealthPlanResult? Plan,
        TraceNode? Trace,
        bool Degraded);
}
