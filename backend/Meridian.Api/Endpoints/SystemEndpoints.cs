using System.Security.Claims;
using Meridian.Api.Auth;
using Meridian.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Api.Endpoints;

public record HealthResponse(string Status);
public record UserDto(string Id, string Name, string Role);

public static class SystemEndpoints
{
    public static void MapSystemEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/health", async (MeridianDbContext db) =>
                await db.Database.CanConnectAsync()
                    ? Results.Ok(new HealthResponse("ok"))
                    : Results.Problem(title: "Database unreachable", statusCode: StatusCodes.Status503ServiceUnavailable))
            .WithName("GetHealth").WithTags("System")
            .Produces<HealthResponse>()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // anonymous on purpose: the UI user switcher needs it before anyone is selected
        api.MapGet("/users", async (MeridianDbContext db) =>
                await db.Users.OrderBy(u => u.Name)
                    .Select(u => new UserDto(u.Id, u.Name, u.Role.ToString()))
                    .ToListAsync())
            .WithName("GetUsers").WithTags("System");

        api.MapGet("/me", (ClaimsPrincipal user) => new UserDto(
                user.FindFirstValue(ClaimTypes.NameIdentifier)!,
                user.FindFirstValue(ClaimTypes.Name)!,
                user.FindFirstValue(ClaimTypes.Role)!))
            .RequireAuthorization(Policies.Analyst)
            .WithName("GetMe").WithTags("System")
            .ProducesProblem(StatusCodes.Status401Unauthorized);
    }
}
