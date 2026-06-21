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

    [Fact]
    public async Task Flights_change_the_calorie_target_over_http()
    {
        // Exercises the real config/plan/flights/*.json files loaded by the host — same goal+profile,
        // different calorie target, no recompile.
        var client = _factory.CreateClient();

        var aggressive = await CalorieTargetWithFlight(client, "conv-agg", "aggressive-plan");
        var conservative = await CalorieTargetWithFlight(client, "conv-con", "conservative-plan");

        Assert.True(aggressive < conservative, $"aggressive {aggressive} should be < conservative {conservative}");
    }

    [Fact]
    public async Task Break_plan_generator_degrades_without_crashing()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/plan", new
        {
            conversationId = "conv-broken",
            action = "Create",
            goal = "LoseFat",
            breakPlanGenerator = true,
            profile = new
            {
                ageYears = 30, sex = "Male", weightKg = 90.0, heightCm = 180.0,
                activityLevel = "Moderate", targetDays = 84, goalWeightKg = 80.0,
            },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // degrades, never 500
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("degraded").GetBoolean());
        Assert.True(body.GetProperty("plan").GetProperty("degraded").GetBoolean());
        Assert.Contains("plan_generator", body.GetProperty("trace").GetRawText()); // the failed tool is in the trace
    }

    private static async Task<int> CalorieTargetWithFlight(HttpClient client, string conversationId, string flight)
    {
        var body = new
        {
            conversationId,
            action = "Create",
            goal = "LoseFat",
            flights = new[] { flight },
            profile = new
            {
                ageYears = 30, sex = "Male", weightKg = 90.0, heightCm = 180.0,
                activityLevel = "Moderate", targetDays = 84, goalWeightKg = 80.0,
            },
        };
        var response = await client.PostAsJsonAsync("/plan", body);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("plan").GetProperty("dailyCalorieTarget").GetInt32();
    }
}
