using System.Diagnostics;
using GarajeMetria.Application.Authentication;
using MediatR;
using Microsoft.Extensions.Logging;

namespace GarajeMetria.Application.Diagnostics;

public sealed class LoggingBehavior<TRequest, TResponse>(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        // Start an activity even without a listener, so background calls can be correlated too.
        using var activity = Activity.Current is null ? new Activity("GarajeMetria.UseCase").SetIdFormat(ActivityIdFormat.W3C).Start() : null;
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["Operation"] = typeof(TRequest).Name,
            ["TraceId"] = Activity.Current?.TraceId.ToString()
        });
        var started = Stopwatch.GetTimestamp();
        logger.LogDebug("Use case started: {Operation}", typeof(TRequest).Name);
        try
        {
            var response = await next(cancellationToken);
            var outcome = response as IOutcome;
            logger.LogInformation("Use case completed: {Operation}, outcome={Outcome}, elapsed={ElapsedMs} ms",
                typeof(TRequest).Name, outcome is null ? "completed" : outcome.Succeeded ? "success" : outcome.Error!.Code.ToString(),
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Use case cancelled: {Operation}, elapsed={ElapsedMs} ms", typeof(TRequest).Name,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    }
}
