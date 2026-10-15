using Meridian.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// one line per log entry, including scopes (correlation id)
builder.Logging.AddSimpleConsole(o =>
{
    o.IncludeScopes = true;
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new() { Title = "Meridian Vote Tracker API", Version = "v1" });
});

// RFC 7807 for every error response; traceId = our correlation id
builder.Services.AddProblemDetails(o =>
{
    o.CustomizeProblemDetails = ctx =>
        ctx.ProblemDetails.Extensions["traceId"] = ctx.HttpContext.TraceIdentifier;
});

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();   // unhandled exception -> 500 ProblemDetails
app.UseStatusCodePages();    // empty 4xx/5xx (e.g. 404) -> ProblemDetails

// Swagger is on in every environment so reviewers can use it from docker
app.UseSwagger();
app.UseSwaggerUI();

var api = app.MapGroup("/api");

api.MapGet("/health", () => Results.Ok(new HealthResponse("ok")))
    .WithName("GetHealth")
    .WithTags("Health")
    .WithOpenApi();

app.Run();

public record HealthResponse(string Status);

// lets the test project start the app in memory (WebApplicationFactory<Program>)
public partial class Program { }
