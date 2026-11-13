using GarajeMetria.Api.Authentication;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Lpr;

namespace GarajeMetria.Api.Lpr;

public enum LprTraffic { Token, Event, Administration }
public sealed record LprTrafficMetadata(LprTraffic Traffic);

public static class LprRateLimits
{
    public static async Task<bool> AdmitAsync(HttpContext context, ILprRateLimitStore store, LprOptions options,
        LprTraffic traffic, string policy, string subject, ILogger? logger = null)
    {
        try
        {
            var decision = await store.AdmitAsync(options.Policy(policy), subject, context.RequestAborted);
            if (decision.Allowed) return true;
            context.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await ErrorAsync(context, traffic, 429);
        }
        catch (PersistenceFailureException ex)
        {
            logger?.LogWarning("LPR quota failed: category={Category}", ex.Code);
            await ErrorAsync(context, traffic, ex.Code == FailureCode.ServiceUnavailable ? 503 : 500);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !context.Response.HasStarted)
        {
            logger?.LogError("LPR quota failed: exceptionType={ExceptionType}", ex.GetType().Name);
            await ErrorAsync(context, traffic, 500);
        }
        return false;
    }

    internal static Task ErrorAsync(HttpContext context, LprTraffic traffic, int status)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (traffic == LprTraffic.Token)
        {
            context.Response.StatusCode = status;
            context.Response.Headers.Pragma = "no-cache";
            return context.Response.WriteAsJsonAsync(new { error = status == 500 ? "server_error" : "temporarily_unavailable" }, context.RequestAborted);
        }
        return (status == 429 ? ApiProblems.Create(429, "rate_limited", "Demasiadas solicitudes. Inténtalo de nuevo más tarde.") :
            ApiProblems.FromFailure(new(status == 503 ? FailureCode.ServiceUnavailable : FailureCode.InternalError))).ExecuteAsync(context);
    }
}

// Endpoint metadata activates the gate when T003-10/11/14 publish the corresponding routes.
public sealed class LprIpRateLimitMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ILprRateLimitStore store, LprOptions options, ILogger<LprIpRateLimitMiddleware> logger)
    {
        var metadata = context.GetEndpoint()?.Metadata.GetMetadata<LprTrafficMetadata>() ??
            (LprProblems.IsTokenRequest(context) ? new LprTrafficMetadata(LprTraffic.Token) : null);
        if (metadata is null) { await next(context); return; }
        var policy = metadata.Traffic == LprTraffic.Token ? "lpr:tokens-ip" : "lpr:events-ip";
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var original = context.RequestAborted;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(original);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
        context.RequestAborted = timeout.Token;
        try
        {
            if (metadata.Traffic == LprTraffic.Administration ||
                await LprRateLimits.AdmitAsync(context, store, options, metadata.Traffic, policy, ip, logger)) await next(context);
        }
        catch (OperationCanceledException) when (!original.IsCancellationRequested && timeout.IsCancellationRequested && !context.Response.HasStarted)
        {
            context.RequestAborted = original;
            await LprRateLimits.ErrorAsync(context, metadata.Traffic, 503);
        }
        finally { context.RequestAborted = original; }
    }
}
