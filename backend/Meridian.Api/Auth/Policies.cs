using Meridian.Domain;
using Microsoft.AspNetCore.Authorization;

namespace Meridian.Api.Auth;

// Admin can do everything an Analyst can, plus positions/weights and publishing.
public static class Policies
{
    public const string Analyst = "Analyst";
    public const string Admin = "Admin";

    public static void Add(AuthorizationOptions o)
    {
        o.AddPolicy(Analyst, p => p.RequireRole(nameof(UserRole.Analyst), nameof(UserRole.Admin)));
        o.AddPolicy(Admin, p => p.RequireRole(nameof(UserRole.Admin)));
    }
}
