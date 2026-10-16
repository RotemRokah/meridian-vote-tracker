using System.Security.Claims;
using Meridian.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Tests.Integration;

// Checks the policies as registered in the real app.
// The HTTP 403 test comes with the first Admin endpoint (PUT /votes/{id}/position, step 7).
[Collection(IntegrationCollection.Name)]
public class AuthorizationPolicyTests
{
    private readonly IAuthorizationService _auth;

    public AuthorizationPolicyTests(ApiFactory factory)
    {
        _auth = factory.Services.GetRequiredService<IAuthorizationService>();
    }

    private static ClaimsPrincipal UserWithRole(string role) =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, role) }, UserIdAuthenticationHandler.SchemeName));

    [Theory]
    [InlineData("Analyst", Policies.Analyst, true)]
    [InlineData("Admin", Policies.Analyst, true)]
    [InlineData("Analyst", Policies.Admin, false)]
    [InlineData("Admin", Policies.Admin, true)]
    public async Task Role_meets_policy(string role, string policy, bool allowed)
    {
        var result = await _auth.AuthorizeAsync(UserWithRole(role), policy);
        Assert.Equal(allowed, result.Succeeded);
    }
}
