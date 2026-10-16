using Meridian.Api.Auth;
using Meridian.Api.Conflicts;
using Meridian.Api.Data;
using Meridian.Api.Endpoints;
using System.Text.Json.Serialization;
using Meridian.Api.Imports;
using Meridian.Api.Infrastructure;
using Meridian.Api.Storage;
using Meridian.Api.Votes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// one line per log entry, including scopes (correlation id)
builder.Logging.AddSimpleConsole(o =>
{
    o.IncludeScopes = true;
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

// Connection string comes from config: env var ConnectionStrings__Default (docker)
// or dotnet user-secrets (local). Read when the DbContext is created, so tests can override it.
builder.Services.AddDbContext<MeridianDbContext>((sp, o) =>
    o.UseNpgsql(sp.GetRequiredService<IConfiguration>().GetConnectionString("Default"))
     .UseSnakeCaseNamingConvention());

builder.Services.AddAuthentication(UserIdAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, UserIdAuthenticationHandler>(UserIdAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization(Policies.Add);

// Imports: file store, background worker, and room for large uploads (historic file is ~103 MB)
builder.Services.AddSingleton<IFileStore, LocalFileStore>();
builder.Services.AddScoped<ImportProcessor>();
builder.Services.AddHostedService<ImportWorker>();
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = ImportEndpoints.MaxUploadBytes);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = ImportEndpoints.MaxUploadBytes);

// Enums travel as names ("Pending", not 0): readable JSON, and the generated TS client gets string enums
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(o =>
    o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));   // Swagger reads these

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new() { Title = "Meridian Vote Tracker API", Version = "v1" });

    // "Authorize" button in Swagger UI: type dana / omer / lena
    var scheme = new OpenApiSecurityScheme
    {
        Name = UserIdAuthenticationHandler.HeaderName,
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Description = "User id: dana, omer (Analyst) or lena (Admin)",
        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "UserId" },
    };
    o.AddSecurityDefinition("UserId", scheme);
    o.AddSecurityRequirement(new OpenApiSecurityRequirement { [scheme] = Array.Empty<string>() });
});

// RFC 7807 for every error response; traceId = our correlation id
builder.Services.AddProblemDetails(o =>
{
    o.CustomizeProblemDetails = ctx =>
        ctx.ProblemDetails.Extensions["traceId"] = ctx.HttpContext.TraceIdentifier;
});
builder.Services.AddExceptionHandler<BadRequestExceptionHandler>();

var app = builder.Build();

// Apply migrations and seed before serving requests.
// Fine for this assignment; in production migrations would be a separate deploy step.
if (string.IsNullOrWhiteSpace(app.Configuration.GetConnectionString("Default")))
    throw new InvalidOperationException(
        "Missing connection string 'ConnectionStrings:Default' (env var ConnectionStrings__Default or dotnet user-secrets).");

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MeridianDbContext>();
    db.Database.Migrate();
    DbSeeder.Seed(db, Path.Combine(AppContext.BaseDirectory, "Seed", "member_states.csv"), app.Logger);
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();   // unhandled exception -> 500 ProblemDetails
app.UseStatusCodePages();    // empty 4xx/5xx (e.g. 404) -> ProblemDetails

// Swagger is on in every environment so reviewers can use it from docker
app.UseSwagger();
app.UseSwaggerUI();

app.UseAuthentication();
app.UseAuthorization();

var api = app.MapGroup("/api");
api.MapSystemEndpoints();
api.MapImportEndpoints();
api.MapConflictEndpoints();
api.MapVoteEndpoints();

app.Run();

// lets the test project start the app in memory (WebApplicationFactory<Program>)
public partial class Program { }
