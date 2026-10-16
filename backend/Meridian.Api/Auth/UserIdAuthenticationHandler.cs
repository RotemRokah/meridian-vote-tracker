using System.Security.Claims;
using System.Text.Encodings.Web;
using Meridian.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Meridian.Api.Auth;

// Simplified auth (spec: production auth is out of scope).
// The client sends "X-User-Id: dana"; we look the user up and build a principal with their role.
public class UserIdAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "UserId";
    public const string HeaderName = "X-User-Id";

    private readonly MeridianDbContext _db;
    private readonly IProblemDetailsService _problemDetails;

    public UserIdAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
        MeridianDbContext db, IProblemDetailsService problemDetails)
        : base(options, logger, encoder)
    {
        _db = db;
        _problemDetails = problemDetails;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var userId = Request.Headers[HeaderName].FirstOrDefault();
        // no header = anonymous; endpoints that need a user answer 401
        if (string.IsNullOrWhiteSpace(userId))
            return AuthenticateResult.NoResult();

        var user = await _db.Users.FindAsync(userId.Trim());
        if (user == null)
            return AuthenticateResult.Fail($"Unknown user: {userId}");

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Name, user.Name),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    // 401 and 403 as ProblemDetails, with a message that says what to do
    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        WriteProblem(StatusCodes.Status401Unauthorized, "Unauthorized",
            $"Send a valid {HeaderName} header (dana, omer or lena).");

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        WriteProblem(StatusCodes.Status403Forbidden, "Forbidden",
            "Your role is not allowed to perform this action.");

    private async Task WriteProblem(int status, string title, string detail)
    {
        Response.StatusCode = status;
        await _problemDetails.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = Context,
            ProblemDetails = { Status = status, Title = title, Detail = detail },
        });
    }
}
