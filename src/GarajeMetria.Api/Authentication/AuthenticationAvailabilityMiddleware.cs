using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Diagnostics;
using Microsoft.AspNetCore.Authorization;

namespace GarajeMetria.Api.Authentication;

public sealed class AuthenticationAvailabilityMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAuthenticationAvailability availability)
    {
        var endpoint = context.GetEndpoint();
        if (GarajeMetria.Api.Lpr.LprProblems.IsTokenRequest(context) ||
            endpoint?.Metadata.GetMetadata<GarajeMetria.Api.Lpr.LprTrafficMetadata>()?.Traffic == GarajeMetria.Api.Lpr.LprTraffic.Event)
        { await next(context); return; }
        var requiresSql = endpoint is not null &&
            (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null ||
             endpoint.Metadata.GetMetadata<AuthEndpointMetadata>() is not null && context.Request.Method == "POST");
        if (requiresSql && !await availability.IsAvailableAsync(context.RequestAborted))
        {
            await ApiProblems.FromFailure(new(FailureCode.ServiceUnavailable)).ExecuteAsync(context);
            return;
        }
        await next(context);
    }
}
