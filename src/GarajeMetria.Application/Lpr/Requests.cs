using GarajeMetria.Application.Authentication;
using GarajeMetria.Domain.Lpr;
using MediatR;

namespace GarajeMetria.Application.Lpr;

public interface ILprAdministrationRequest { }
public interface ILprDeviceRequest
{
    Guid DeviceId { get; }
    string WorkshopId { get; }
}
public interface ILprEventWriteRequest : ILprDeviceRequest { }

public abstract record LprRequest<T> : IRequest<Outcome<T>>, IFailureResponseRequest<Outcome<T>>
{
    Outcome<T> IFailureResponseRequest<Outcome<T>>.Fail(FailureCode code) => Outcome<T>.Fail(code);
    Outcome<T> IFailureResponseRequest<Outcome<T>>.Fail(Failure failure) => new(default, failure);
}

public sealed record RegisterDeviceCommand(string Name, string WorkshopId)
    : LprRequest<DeviceProvisioningResult>, ILprAdministrationRequest
{
    public string? LocationDescription { get; init; }
    public override string ToString() => nameof(RegisterDeviceCommand);
}
public sealed record UpdateDeviceLocationCommand(Guid DeviceId, string LocationDescription, Guid ExpectedVersion)
    : LprRequest<DeviceMutationResult>, ILprAdministrationRequest
{
    public override string ToString() => nameof(UpdateDeviceLocationCommand);
}
public sealed record RenameDeviceCommand(Guid DeviceId, string Name, Guid ExpectedVersion)
    : LprRequest<DeviceMutationResult>, ILprAdministrationRequest;
public sealed record DeactivateDeviceCommand(Guid DeviceId, Guid ExpectedVersion)
    : LprRequest<DeviceMutationResult>, ILprAdministrationRequest;
public sealed record ReactivateDeviceCommand(Guid DeviceId, Guid ExpectedVersion)
    : LprRequest<DeviceProvisioningResult>, ILprAdministrationRequest;
public sealed record RotateDeviceCredentialsCommand(Guid DeviceId, Guid ExpectedVersion)
    : LprRequest<DeviceRotationResult>, ILprAdministrationRequest;
public sealed record RevokeDeviceCredentialsCommand(Guid DeviceId, Guid ExpectedVersion)
    : LprRequest<DeviceMutationResult>, ILprAdministrationRequest;
public sealed record ListDevicesQuery(int PageNumber = 1, int PageSize = 20, string? WorkshopId = null, bool? IsActive = null)
    : LprRequest<DevicePage>, ILprAdministrationRequest;
public sealed record GetDeviceQuery(Guid DeviceId) : LprRequest<DeviceDto>, ILprAdministrationRequest;
public sealed record ReceiveLprEventCommand(Guid EventId, Guid DeviceId, string WorkshopId, DateTimeOffset CapturedAt,
    string PlateOriginal, decimal Confidence, LprDirection? Direction = null)
    : LprRequest<ReceiveLprEventResult>, ILprEventWriteRequest
{
    public override string ToString() => nameof(ReceiveLprEventCommand);
}
public sealed record PrepareDeviceTokenGrantCommand(string ClientId, string ClientSecret) : LprRequest<DeviceTokenGrant>
{
    public override string ToString() => nameof(PrepareDeviceTokenGrantCommand);
}
public sealed record ValidateDeviceAccessQuery(Guid DeviceId, string WorkshopId)
    : LprRequest<DeviceAuthorizationState>;
