namespace Meridian.Api.Infrastructure;

// Reads X-Correlation-ID from the request (or creates one), echoes it in the
// response, and puts it in the log scope so every log line of this request carries it.
public class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-ID";

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[HeaderName].FirstOrDefault();
        // ignore empty or absurdly long values coming from the client
        if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 100)
            correlationId = Guid.NewGuid().ToString();

        // TraceIdentifier is what ProblemDetails and ASP.NET logs use as the request id
        context.TraceIdentifier = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        // a message template (not a Dictionary): the console prints it as "CorrelationId:abc",
        // and structured log sinks still get CorrelationId as a field
        using (_logger.BeginScope("CorrelationId:{CorrelationId}", correlationId))
        {
            await _next(context);
        }
    }
}
