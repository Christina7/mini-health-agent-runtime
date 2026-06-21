using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace HealthAgents.Web.Tests;

/// <summary>
/// Pins the static-page routing: the guided walkthrough is the landing page at "/", and the live
/// chat app is served at "/app". (The triage behavior itself is covered by <see cref="TriageEndpointTests"/>.)
/// </summary>
public class RoutingTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RoutingTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Root_serves_the_walkthrough_page()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("project walkthrough", html); // the walkthrough page's <title>
    }

    [Fact]
    public async Task App_path_serves_the_chat_app()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/app");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Describe your symptoms", html); // the chat app's input placeholder
    }
}
