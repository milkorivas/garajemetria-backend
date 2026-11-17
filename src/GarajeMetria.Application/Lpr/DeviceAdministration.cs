using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Users;
using GarajeMetria.Domain.Authentication;
using GarajeMetria.Domain.Lpr;
using MediatR;

namespace GarajeMetria.Application.Lpr;

public sealed class DeviceAdministration(ILprDeviceRepository devices, ILprDeviceAdministrationQuery queries,
    IWorkshopQuery workshops, IDeviceCredentialService credentials, IDeviceAdministrationAuditRepository audits,
    ILprUnitOfWork unitOfWork, ILprClock clock, ICurrentUserContext context,
    IAuthenticationSessionRepository sessions, ILprRateLimitStore limits, LprOptions options)
{
    private async Task<Failure?> CheckAsync(bool write, CancellationToken ct)
    {
        var now = await clock.GetUtcNowAsync(ct);
        var actor = context.User;
        if (actor is null) return new(FailureCode.SessionEnded);
        var session = await sessions.FindAsync(actor.SessionId, ct);
        if (session?.Allows(actor.Id, actor.Role, now) != true) return new(FailureCode.SessionEnded);
        if (actor.Role != UserRoles.Administrator) return new(FailureCode.Forbidden);
        if (write)
        {
            var permit = await limits.AdmitAsync(options.Policy("lpr:admin-user"), actor.Id.ToString("N"), ct);
            if (!permit.Allowed) return new(FailureCode.RateLimited, permit.RetryAfterSeconds);
        }
        return null;
    }

    internal async Task<Outcome<DevicePage>> ListAsync(ListDevicesQuery request, CancellationToken ct)
    {
        if (await CheckAsync(false, ct) is { } error) return new(null, error);
        return Outcome<DevicePage>.Success(await queries.ListAsync(request.PageNumber, request.PageSize, request.WorkshopId, request.IsActive, ct));
    }

    internal async Task<Outcome<DeviceDto>> GetAsync(GetDeviceQuery request, CancellationToken ct)
    {
        if (await CheckAsync(false, ct) is { } error) return new(null, error);
        var device = await queries.FindAsync(request.DeviceId, ct);
        return device is null ? Outcome<DeviceDto>.Fail(FailureCode.ResourceNotFound) : Outcome<DeviceDto>.Success(device);
    }

    internal async Task<Outcome<DeviceProvisioningResult>> RegisterAsync(RegisterDeviceCommand request, CancellationToken ct)
    {
        if (await CheckAsync(true, ct) is { } denied) return new(null, denied);
        var actor = context.User!;
        var id = Guid.NewGuid();
        return await unitOfWork.ExecuteAsync(new(actor.Id, id), async token =>
        {
            if (await CheckAsync(false, token) is { } error) return new Outcome<DeviceProvisioningResult>(null, error);
            if (await ActiveWorkshopAsync(request.WorkshopId, token) is { } unavailable) return Outcome<DeviceProvisioningResult>.Fail(unavailable);
            var now = await clock.GetUtcNowAsync(token);
            var device = new LprDevice(id, request.Name, request.WorkshopId, true, now);
            devices.Add(device);
            var generated = await credentials.CreateAsync(id, token);
            if (request.LocationDescription is not null)
            {
                var version = device.Version;
                device.ChangeLocation(request.LocationDescription, now);
                if (!await devices.SaveAsync(device, version, token)) return Outcome<DeviceProvisioningResult>.Fail(FailureCode.PreconditionFailed);
            }
            Audit(DeviceAdministrationOperation.Created, null, device, now, actor.Id);
            return Outcome<DeviceProvisioningResult>.Success(new(Dto(device), generated));
        }, ct);
    }

    internal async Task<Outcome<DeviceChange>> ChangeAsync(Guid id, Guid expectedVersion,
        DeviceAdministrationOperation operation, string? name, CancellationToken ct)
    {
        if (await CheckAsync(true, ct) is { } denied) return new(null, denied);
        var actor = context.User!;
        return await unitOfWork.ExecuteAsync(new(actor.Id, id), async token =>
        {
            if (await CheckAsync(false, token) is { } error) return new Outcome<DeviceChange>(null, error);
            var device = await devices.FindAsync(id, token);
            if (device is null) return Outcome<DeviceChange>.Fail(FailureCode.ResourceNotFound);
            if (device.Version != expectedVersion) return Outcome<DeviceChange>.Fail(FailureCode.PreconditionFailed);
            if (operation is DeviceAdministrationOperation.CredentialsRotated or DeviceAdministrationOperation.Reactivated)
            {
                if (operation == DeviceAdministrationOperation.CredentialsRotated && !device.IsActive ||
                    operation == DeviceAdministrationOperation.Reactivated && device.IsActive)
                    return Outcome<DeviceChange>.Fail(FailureCode.DeviceConflict);
                if (await ActiveWorkshopAsync(device.WorkshopId, token) is { } unavailable) return Outcome<DeviceChange>.Fail(unavailable);
            }
            var before = device.Snapshot();
            var now = await clock.GetUtcNowAsync(token);
            var changed = operation switch
            {
                DeviceAdministrationOperation.Renamed => device.Rename(name!, now),
                DeviceAdministrationOperation.LocationChanged => device.ChangeLocation(name!, now),
                DeviceAdministrationOperation.Deactivated => device.Deactivate(now),
                DeviceAdministrationOperation.CredentialsRevoked => device.RevokeCredentials(now),
                DeviceAdministrationOperation.Reactivated => Reactivate(device, now),
                DeviceAdministrationOperation.CredentialsRotated => Rotate(device, now),
                _ => throw new InvalidOperationException("Unsupported device transition.")
            };
            if (!changed) return Outcome<DeviceChange>.Success(new(Dto(device), null));
            if (!await devices.SaveAsync(device, expectedVersion, token)) return Outcome<DeviceChange>.Fail(FailureCode.PreconditionFailed);
            DeviceCredentials? generated = null;
            if (operation is DeviceAdministrationOperation.Reactivated or DeviceAdministrationOperation.CredentialsRotated)
                generated = await credentials.RotateAsync(id, token);
            if (operation is DeviceAdministrationOperation.Deactivated or DeviceAdministrationOperation.CredentialsRevoked)
                await credentials.RevokeAsync(id, token);
            Audit(operation, before, device, now, actor.Id);
            return Outcome<DeviceChange>.Success(new(Dto(device), generated));
        }, ct);
    }

    private async Task<FailureCode?> ActiveWorkshopAsync(string id, CancellationToken ct) => await workshops.CheckAsync(id, ct) switch
    {
        WorkshopAvailability.Active => null,
        WorkshopAvailability.CatalogUnavailable => FailureCode.ServiceUnavailable,
        WorkshopAvailability.Missing => FailureCode.ResourceNotFound,
        _ => FailureCode.DeviceConflict
    };
    private void Audit(DeviceAdministrationOperation operation, DeviceAdministrationSnapshot? previous, LprDevice device,
        DateTimeOffset now, Guid actor) => audits.Add(new(Guid.NewGuid(), actor, operation, previous, device.Snapshot(), now, context.CorrelationId));
    private static bool Reactivate(LprDevice device, DateTimeOffset now) { device.Reactivate(true, now); return true; }
    private static bool Rotate(LprDevice device, DateTimeOffset now) { device.RotateCredentials(true, now); return true; }
    private static DeviceDto Dto(LprDevice d) => new(d.Id, d.Name, d.WorkshopId, d.IsActive, d.HasCredentials, d.CreatedAt, d.UpdatedAt, d.Version)
    { LocationDescription = d.Location?.Description, LocationRecordedAt = d.Location?.RecordedAt };
}

public sealed class UpdateDeviceLocationHandler(DeviceAdministration service) : IRequestHandler<UpdateDeviceLocationCommand, Outcome<DeviceMutationResult>>
{
    public async Task<Outcome<DeviceMutationResult>> Handle(UpdateDeviceLocationCommand request, CancellationToken ct)
    {
        var result = await service.ChangeAsync(request.DeviceId, request.ExpectedVersion, DeviceAdministrationOperation.LocationChanged,
            request.LocationDescription, ct);
        return result.Succeeded ? Outcome<DeviceMutationResult>.Success(new(result.Value!.Device.Version)) : new(null, result.Error);
    }
}

internal sealed record DeviceChange(DeviceDto Device, DeviceCredentials? Credentials)
{
    public override string ToString() => nameof(DeviceChange);
}

public sealed class RegisterDeviceHandler(DeviceAdministration service) : IRequestHandler<RegisterDeviceCommand, Outcome<DeviceProvisioningResult>>
{
    public Task<Outcome<DeviceProvisioningResult>> Handle(RegisterDeviceCommand request, CancellationToken ct) => service.RegisterAsync(request, ct);
}
public sealed class ListDevicesHandler(DeviceAdministration service) : IRequestHandler<ListDevicesQuery, Outcome<DevicePage>>
{
    public Task<Outcome<DevicePage>> Handle(ListDevicesQuery request, CancellationToken ct) => service.ListAsync(request, ct);
}
public sealed class GetDeviceHandler(DeviceAdministration service) : IRequestHandler<GetDeviceQuery, Outcome<DeviceDto>>
{
    public Task<Outcome<DeviceDto>> Handle(GetDeviceQuery request, CancellationToken ct) => service.GetAsync(request, ct);
}
public sealed class RenameDeviceHandler(DeviceAdministration service) : IRequestHandler<RenameDeviceCommand, Outcome<DeviceMutationResult>>
{
    public async Task<Outcome<DeviceMutationResult>> Handle(RenameDeviceCommand r, CancellationToken ct)
    {
        var result = await service.ChangeAsync(r.DeviceId, r.ExpectedVersion, DeviceAdministrationOperation.Renamed, r.Name, ct);
        return result.Succeeded ? Outcome<DeviceMutationResult>.Success(new(result.Value!.Device.Version)) : new(null, result.Error);
    }
}
public sealed class DeactivateDeviceHandler(DeviceAdministration service) : IRequestHandler<DeactivateDeviceCommand, Outcome<DeviceMutationResult>>
{
    public async Task<Outcome<DeviceMutationResult>> Handle(DeactivateDeviceCommand r, CancellationToken ct)
    {
        var result = await service.ChangeAsync(r.DeviceId, r.ExpectedVersion, DeviceAdministrationOperation.Deactivated, null, ct);
        return result.Succeeded ? Outcome<DeviceMutationResult>.Success(new(result.Value!.Device.Version)) : new(null, result.Error);
    }
}
public sealed class RevokeDeviceCredentialsHandler(DeviceAdministration service) : IRequestHandler<RevokeDeviceCredentialsCommand, Outcome<DeviceMutationResult>>
{
    public async Task<Outcome<DeviceMutationResult>> Handle(RevokeDeviceCredentialsCommand r, CancellationToken ct)
    {
        var result = await service.ChangeAsync(r.DeviceId, r.ExpectedVersion, DeviceAdministrationOperation.CredentialsRevoked, null, ct);
        return result.Succeeded ? Outcome<DeviceMutationResult>.Success(new(result.Value!.Device.Version)) : new(null, result.Error);
    }
}
public sealed class ReactivateDeviceHandler(DeviceAdministration service) : IRequestHandler<ReactivateDeviceCommand, Outcome<DeviceProvisioningResult>>
{
    public async Task<Outcome<DeviceProvisioningResult>> Handle(ReactivateDeviceCommand r, CancellationToken ct)
    {
        var result = await service.ChangeAsync(r.DeviceId, r.ExpectedVersion, DeviceAdministrationOperation.Reactivated, null, ct);
        return result.Succeeded ? Outcome<DeviceProvisioningResult>.Success(new(result.Value!.Device, result.Value.Credentials!)) : new(null, result.Error);
    }
}
public sealed class RotateDeviceCredentialsHandler(DeviceAdministration service) : IRequestHandler<RotateDeviceCredentialsCommand, Outcome<DeviceRotationResult>>
{
    public async Task<Outcome<DeviceRotationResult>> Handle(RotateDeviceCredentialsCommand r, CancellationToken ct)
    {
        var result = await service.ChangeAsync(r.DeviceId, r.ExpectedVersion, DeviceAdministrationOperation.CredentialsRotated, null, ct);
        return result.Succeeded ? Outcome<DeviceRotationResult>.Success(new(result.Value!.Credentials!, result.Value.Device.Version)) : new(null, result.Error);
    }
}
