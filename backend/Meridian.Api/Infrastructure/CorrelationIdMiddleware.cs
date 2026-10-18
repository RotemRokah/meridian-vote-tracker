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
        // on the request's telemetry too: Application Insights shows it as customDimensions.CorrelationId
        System.Diagnostics.Activity.Current?.SetTag("CorrelationId", correlationId);

        // a message template (not a Dictionary): the console prints it as "CorrelationId:abc",
        // and structured log sinks still get CorrelationId as a field
        using (_logger.BeginScope("CorrelationId:{CorrelationId}", correlationId))
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            await _next(context);
            // one line per request: every request can be found by its correlation id, not only the ones
            // that happen to log something. CorrelationId is a message parameter on purpose: exporters
            // always send parameters as properties; scopes depend on exporter settings (they didn't arrive).
            _logger.LogInformation("HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs:F0} ms, correlation {CorrelationId}",
                context.Request.Method, context.Request.Path.Value, context.Response.StatusCode,
                System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, correlationId);
        }
    }
}
