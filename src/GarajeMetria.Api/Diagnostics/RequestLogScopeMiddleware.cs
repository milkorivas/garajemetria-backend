using System.Diagnostics;
using GarajeMetria.Application.Diagnostics;

namespace GarajeMetria.Api.Diagnostics;

public sealed class RequestLogScopeMiddleware(RequestDelegate next, ILogger<RequestLogScopeMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["RequestId"] = context.TraceIdentifier,
            ["TraceId"] = Activity.Current?.TraceId.ToString()
        });
        try { await next(context); }
        catch (Exception exception)
        {
            // Includes failures after headers were sent or while writing ProblemDetails.
            logger.LogSafeException(LogLevel.Error, "Http.Unhandled", exception);
            throw;
        }
    }
}

public sealed class RequestCancellationLoggingMiddleware(RequestDelegate next, ILogger<RequestCancellationLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try { await next(context); }
        catch (Exception exception) when (context.RequestAborted.IsCancellationRequested &&
            exception is OperationCanceledException or IOException)
        {
            // Observe before UseExceptionHandler consumes the client-disconnect exception.
            logger.LogSafeException(LogLevel.Information, "Http.Cancelled", exception);
            throw;
        }
    }
}
