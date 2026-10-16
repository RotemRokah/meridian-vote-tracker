using Microsoft.AspNetCore.Diagnostics;

namespace Meridian.Api.Infrastructure;

// A malformed request (broken form body, upload over the size limit) is the client's fault.
// ASP.NET signals it with BadHttpRequestException and the right status (400, 413);
// without this handler UseExceptionHandler would turn it into a 500.
public class BadRequestExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetails;

    public BadRequestExceptionHandler(IProblemDetailsService problemDetails)
    {
        _problemDetails = problemDetails;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is not BadHttpRequestException bad)
            return false;   // anything else stays a 500

        context.Response.StatusCode = bad.StatusCode;
        return await _problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = { Status = bad.StatusCode, Title = "Bad request", Detail = bad.Message },
        });
    }
}
