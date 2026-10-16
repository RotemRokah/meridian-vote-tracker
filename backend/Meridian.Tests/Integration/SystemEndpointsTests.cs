using System.Net;
using System.Net.Http.Json;
using Meridian.Api.Endpoints;
using Microsoft.AspNetCore.Mvc;

namespace Meridian.Tests.Integration;

[Collection(IntegrationCollection.Name)]
public class SystemEndpointsTests
{
    private readonly HttpClient _client;

    public SystemEndpointsTests(ApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    private static HttpRequestMessage Get(string url, string? userId = null, string? correlationId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (userId != null) request.Headers.Add("X-User-Id", userId);
        if (correlationId != null) request.Headers.Add("X-Correlation-ID", correlationId);
        return request;
    }

    [Fact]
    public async Task Health_returns_ok_when_database_is_reachable()
    {
        var response = await _client.GetFromJsonAsync<HealthResponse>("/api/health");
        Assert.Equal("ok", response!.Status);
    }

    [Fact]
    public async Task Correlation_id_from_request_is_echoed()
    {
        var response = await _client.SendAsync(Get("/api/health", correlationId: "test-123"));
        Assert.Equal("test-123", response.Headers.GetValues("X-Correlation-ID").Single());
    }

    [Fact]
    public async Task Unknown_route_returns_problem_details_with_trace_id()
    {
        var response = await _client.SendAsync(Get("/api/does-not-exist", correlationId: "test-404"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("test-404", problem!.Extensions["traceId"]!.ToString());
    }

    [Fact]
    public async Task Users_lists_the_three_seeded_users_without_auth()
    {
        var users = await _client.GetFromJsonAsync<List<UserDto>>("/api/users");

        Assert.Equal(
            new[] { ("dana", "Analyst"), ("lena", "Admin"), ("omer", "Analyst") },
            users!.Select(u => (u.Id, u.Role)).OrderBy(u => u.Id));
    }

    [Theory]
    [InlineData("dana", "Analyst")]
    [InlineData("lena", "Admin")]
    public async Task Me_returns_the_user_from_the_header(string userId, string expectedRole)
    {
        var response = await _client.SendAsync(Get("/api/me", userId));

        response.EnsureSuccessStatusCode();
        var me = await response.Content.ReadFromJsonAsync<UserDto>();
        Assert.Equal(userId, me!.Id);
        Assert.Equal(expectedRole, me.Role);
    }

    [Theory]
    [InlineData(null)]        // no header
    [InlineData("mallory")]   // unknown user
    public async Task Me_without_a_valid_user_is_401_problem_details(string? userId)
    {
        var response = await _client.SendAsync(Get("/api/me", userId));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Contains("X-User-Id", problem!.Detail);
    }
}
