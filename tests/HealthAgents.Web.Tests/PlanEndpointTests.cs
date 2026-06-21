using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace HealthAgents.Web.Tests;

/// <summary>
/// Drives the plan agent over HTTP through the real host (the plan-side mirror of
/// <see cref="TriageEndpointTests"/>). Proves the second agent runs on the same runtime: a create
/// returns a structured plan with a multi-tool <c>plan.turn</c> trace; an unsafe goal escalates with
/// no plan; the same conversation accumulates progress across a create then a log; and a malformed
/// request is a 400.
/// </summary>
public class PlanEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public PlanEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private static object CreateBody(string conversationId, double weightKg = 90, double goalKg = 80, int targetDays = 84) => new
    {
        conversationId,
        action = "Create",
        goal = "LoseFat",
        profile = new
        {
            ageYears = 30, sex = "Male", weightKg, heightCm = 180.0,
            activityLevel = "Moderate", targetDays, goalWeightKg = goalKg,
        },
    };

    [Fact]
    public async Task Create_returns_a_structured_plan_with_a_multi_tool_trace()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/plan", CreateBody("conv-create"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var plan = body.GetProperty("plan");
        Assert.Equal("LoseFat", plan.GetProperty("goal").GetString());
        Assert.True(plan.GetProperty("dailyCalorieTarget").GetInt32() > 0);
        Assert.True(plan.GetProperty("tasks").GetArrayLength() > 0);

        var trace = body.GetProperty("trace");
        Assert.Equal("plan.turn", trace.GetProperty("name").GetString());
        var traceText = trace.GetRawText();
        Assert.Contains("tool:profile_analyzer", traceText);
        Assert.Contains("tool:plan_generator", traceText);
    }

    [Fact]
    public async Task Unsafe_goal_escalates_with_no_plan()
    {
        var client = _factory.CreateClient();

        // Goal weight 50 kg at 180 cm is underweight — the guardrail short-circuits.
        var response = await client.PostAsJsonAsync("/plan", CreateBody("conv-unsafe", weightKg: 55, goalKg: 50));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(JsonValueKind.Null, body.GetProperty("plan").ValueKind);
        Assert.Contains("consult", body.GetProperty("reply").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Same_conversation_create_then_log_accumulates_progress()
    {
        var client = _factory.CreateClient();
        const string id = "conv-multiturn";

        await client.PostAsJsonAsync("/plan", CreateBody(id));

        var logResponse = await client.PostAsJsonAsync("/plan", new
        {
            conversationId = id,
            action = "Log",
            log = new { caloriesLogged = 2200, tasksCompleted = 3 },
        });

        Assert.Equal(HttpStatusCode.OK, logResponse.StatusCode);
        var body = await logResponse.Content.ReadFromJsonAsync<JsonElement>();

        var progress = body.GetProperty("plan").GetProperty("progress");
        Assert.Equal(1, progress.GetArrayLength());
        Assert.Equal(1, progress[0].GetProperty("day").GetInt32());
    }

    [Fact]
    public async Task Create_without_a_profile_is_a_bad_request()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/plan", new { action = "Create", goal = "LoseFat" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
