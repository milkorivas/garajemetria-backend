using GarajeMetria.Application.Authentication;
using MediatR;

namespace GarajeMetria.Application.Lpr;

public sealed record DeviceTokenLifetime(TimeSpan AccessTokenLifetime);

public sealed class PrepareDeviceTokenGrantHandler(IDeviceCredentialService credentials, IDeviceAuthorizationQuery authorization,
    ILprUnitOfWork unitOfWork, ILprClock clock, DeviceTokenLifetime lifetime)
    : IRequestHandler<PrepareDeviceTokenGrantCommand, Outcome<DeviceTokenGrant>>
{
    public Task<Outcome<DeviceTokenGrant>> Handle(PrepareDeviceTokenGrantCommand request, CancellationToken ct)
    {
        var id = Guid.ParseExact(request.ClientId[4..], "N");
        return unitOfWork.ExecuteAsync(new(null, id), async token =>
        {
            if (!await credentials.VerifyAsync(request.ClientId, request.ClientSecret, token))
                return Outcome<DeviceTokenGrant>.Fail(FailureCode.InvalidCredentials);
            var state = await authorization.FindAsync(id, token);
            if (state is null || !state.IsActive || !state.HasCredentials) return Outcome<DeviceTokenGrant>.Fail(FailureCode.InvalidCredentials);
            if (!state.CatalogAvailable) return Outcome<DeviceTokenGrant>.Fail(FailureCode.ServiceUnavailable);
            if (!state.WorkshopIsActive) return Outcome<DeviceTokenGrant>.Fail(FailureCode.InvalidCredentials);
            var now = await clock.GetUtcNowAsync(token);
            return Outcome<DeviceTokenGrant>.Success(new(id, request.ClientId, state.WorkshopId,
                state.AuthorizationVersion, now, now.Add(lifetime.AccessTokenLifetime)));
        }, ct);
    }
}

// Internal coordination for mediated access checks; transport supplies only a verified identity.
public sealed class DeviceAccess(ICurrentDeviceContext context, IDeviceAuthorizationQuery authorization, ILprClock clock)
{
    internal async Task<Outcome<DeviceAuthorizationState>> CheckAsync(Guid deviceId, string workshopId, CancellationToken ct)
        => (await CheckAtAsync(deviceId, workshopId, ct)).Access;

    internal async Task<(Outcome<DeviceAuthorizationState> Access, DateTimeOffset Now)> CheckAtAsync(Guid deviceId, string workshopId, CancellationToken ct)
    {
        var identity = context.Device;
        if (identity is null || identity.DeviceId == Guid.Empty || identity.AuthorizationVersion == Guid.Empty)
            return (Outcome<DeviceAuthorizationState>.Fail(FailureCode.InvalidDeviceToken), default);
        if (!identity.HasEventWriteScope || identity.DeviceId != deviceId || !string.Equals(identity.WorkshopId, workshopId, StringComparison.Ordinal))
            return (Outcome<DeviceAuthorizationState>.Fail(FailureCode.DeviceForbidden), default);
        var state = await authorization.FindAsync(identity.DeviceId, ct);
        var now = await clock.GetUtcNowAsync(ct);
        if (identity.ExpiresAt <= now || state is null || state.DeviceId != identity.DeviceId || !state.IsActive || !state.HasCredentials ||
            state.AuthorizationVersion != identity.AuthorizationVersion || !string.Equals(state.WorkshopId, identity.WorkshopId, StringComparison.Ordinal))
            return (Outcome<DeviceAuthorizationState>.Fail(FailureCode.InvalidDeviceToken), now);
        if (!state.CatalogAvailable) return (Outcome<DeviceAuthorizationState>.Fail(FailureCode.ServiceUnavailable), now);
        if (!state.WorkshopIsActive) return (Outcome<DeviceAuthorizationState>.Fail(FailureCode.InactiveWorkshop), now);
        return (Outcome<DeviceAuthorizationState>.Success(state), now);
    }
}

public sealed class ValidateDeviceAccessHandler(DeviceAccess access) : IRequestHandler<ValidateDeviceAccessQuery, Outcome<DeviceAuthorizationState>>
{
    public Task<Outcome<DeviceAuthorizationState>> Handle(ValidateDeviceAccessQuery request, CancellationToken ct) =>
        access.CheckAsync(request.DeviceId, request.WorkshopId, ct);
}
