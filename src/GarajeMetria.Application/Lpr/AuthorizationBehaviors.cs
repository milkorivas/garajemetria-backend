using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Users;
using MediatR;

namespace GarajeMetria.Application.Lpr;

public sealed class LprAdministrationAuthorizationBehavior<TRequest, TResponse>(UserAdministrationAccess access)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull, ILprAdministrationRequest, IFailureResponseRequest<TResponse>
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) =>
        await access.CheckAsync(cancellationToken) is { } denied ? request.Fail(denied) : await next(cancellationToken);
}

public sealed class LprDeviceAuthorizationBehavior<TRequest, TResponse>(DeviceAccess access) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull, ILprDeviceRequest, IFailureResponseRequest<TResponse>
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var result = await access.CheckAsync(request.DeviceId, request.WorkshopId, cancellationToken);
        if (!result.Succeeded) return request.Fail(result.Error!);
        // This early gate cannot replace revalidation under business locks in T003-09/11/13.
        return await next(cancellationToken);
    }
}

public sealed class LprEventQuotaBehavior<TRequest, TResponse>(ILprRateLimitStore limits, LprOptions options)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull, ILprEventWriteRequest, IFailureResponseRequest<TResponse>
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        var permit = await limits.AdmitAsync(options.Policy("lpr:events-device"), request.DeviceId.ToString("N"), ct);
        // Also charge duplicates: comparison/persistence belongs to the handler after this gate.
        return permit.Allowed ? await next(ct) : request.Fail(new Failure(FailureCode.RateLimited, permit.RetryAfterSeconds));
    }
}
