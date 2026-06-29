using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace HealthAgents.Web.Tests;

/// <summary>
/// Boots the real web host in-memory with <see cref="WebApplicationFactory{T}"/> and drives it over
/// HTTP, end to end through the same runtime the CLI uses. These are the load-bearing slice-1 proofs:
/// the cardiac red-flag escalates with a trace; a high-severity input yields a structured Emergency
/// triage; and a repeated <c>conversationId</c> shows the server-side multi-turn memory.
/// </summary>
public class TriageEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public TriageEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    // The cardiac red-flag rule (chest pain + shortness of breath) trips the always-on guardrail,
    // which short-circuits before planning. The reply escalates to an emergency and the turn still
    // emits a trace tree (triage.turn -> guardrail).
    [Fact]
    public async Task Cardiac_red_flag_escalates_to_emergency_with_a_trace()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/triage", new
        {
            message = "severe chest pain and shortness of breath",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Contains("emergency", body.GetProperty("reply").GetString(), StringComparison.OrdinalIgnoreCase);

        var trace = body.GetProperty("trace");
        Assert.Equal("triage.turn", trace.GetProperty("name").GetString());
        Assert.True(trace.GetProperty("children").GetArrayLength() > 0, "the turn should emit at least one child span");
    }

    // Issue #36 pinned at the runtime layer: "chest pressure" alone (a cardiac chest phrase that was
    // once unknown to the KB and fell to SelfCare) must triage UrgentCare over real HTTP, through the
    // served host wiring — not just in a KB unit test. This is the load-bearing proof that the fix is
    // on the path the host actually runs, which is exactly the gap a unit test cannot close.
    [Fact]
    public async Task Chest_pressure_alone_triages_urgent_care_over_http()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/triage", new
        {
            message = "I have chest pressure right now",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var triage = body.GetProperty("triage");
        Assert.Equal("UrgentCare", triage.GetProperty("urgency").GetString());
        Assert.Contains(
            "symptom_kb",
            triage.GetProperty("toolsInvoked").EnumerateArray().Select(e => e.GetString()));
    }

    // A high-severity (but non-red-flag) input runs the full plan -> act -> observe loop: the planner
    // calls symptom_kb and finishes with a structured TriageResult whose urgency is Emergency.
    [Fact]
    public async Task High_severity_input_returns_a_structured_emergency_triage()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/triage", new
        {
            message = "sore throat and trouble breathing",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var triage = body.GetProperty("triage");
        Assert.Equal("Emergency", triage.GetProperty("urgency").GetString());
        Assert.Contains(
            "symptom_kb",
            triage.GetProperty("toolsInvoked").EnumerateArray().Select(e => e.GetString()));
        Assert.True(body.GetProperty("trace").GetProperty("children").GetArrayLength() > 0);
    }

    // Multi-turn memory over HTTP: reusing the same conversationId threads one server-side
    // WorkContext, so the remembered turn count grows. A fresh session per request would report 1.
    [Fact]
    public async Task Same_conversation_id_accumulates_turns()
    {
        var client = _factory.CreateClient();
        var conversationId = "memory-test-conversation";

        var first = await client.PostAsJsonAsync("/triage", new { conversationId, message = "sore throat" });
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(conversationId, firstBody.GetProperty("conversationId").GetString());
        Assert.Equal(1, firstBody.GetProperty("turnCount").GetInt32());

        var second = await client.PostAsJsonAsync("/triage", new { conversationId, message = "now I also have a headache" });
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(conversationId, secondBody.GetProperty("conversationId").GetString());
        Assert.Equal(2, secondBody.GetProperty("turnCount").GetInt32());
    }

    // A missing/blank message is a client error, not a server fault.
    [Fact]
    public async Task Blank_message_is_a_bad_request()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/triage", new { message = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
