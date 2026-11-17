using GarajeMetria.Application.Authentication;
using GarajeMetria.Domain.Lpr;

namespace GarajeMetria.Application.Lpr;

public sealed record CurrentDevice(Guid DeviceId, string WorkshopId, Guid AuthorizationVersion,
    DateTimeOffset ExpiresAt, bool HasEventWriteScope);
public interface ICurrentDeviceContext
{
    // Populate only from the separately validated device token, never from the event body.
    CurrentDevice? Device { get; }
}

public sealed record DeviceAuthorizationState(Guid DeviceId, string WorkshopId, bool IsActive,
    bool HasCredentials, Guid AuthorizationVersion, bool CatalogAvailable, bool WorkshopIsActive);
public interface IDeviceAuthorizationQuery
{
    // Read current persisted authorization; stale entity caches cannot grant access.
    Task<DeviceAuthorizationState?> FindAsync(Guid deviceId, CancellationToken cancellationToken);
}

public interface ILprDeviceRepository
{
    Task<LprDevice?> FindAsync(Guid deviceId, CancellationToken cancellationToken);
    void Add(LprDevice device);
    Task<bool> SaveAsync(LprDevice device, Guid expectedVersion, CancellationToken cancellationToken);
}

public enum LprEventComparison { Missing, Identical, Conflict }
public sealed record LprEventLookup(LprEventComparison Comparison, LprReceipt? Receipt = null);
public interface ILprEventRepository
{
    // Only return receipt metadata for an identical event of the authorized device/workshop.
    Task<LprEventLookup> CompareAsync(ReceiveLprEventCommand incoming, CancellationToken cancellationToken);
    void Add(LprEvent observation);
}

public interface IDeviceAdministrationAuditRepository
{
    void Add(DeviceAdministrationAuditEntry entry);
}

public sealed record DeviceDto(Guid Id, string Name, string WorkshopId, bool IsActive, bool HasCredentials,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Guid Version)
{
    public string? LocationDescription { get; init; }
    public DateTimeOffset? LocationRecordedAt { get; init; }
    public override string ToString() => nameof(DeviceDto);
}
public sealed record DevicePage(IReadOnlyList<DeviceDto> Items, int PageNumber, int PageSize, int TotalCount);
public interface ILprDeviceAdministrationQuery
{
    Task<DevicePage> ListAsync(int pageNumber, int pageSize, string? workshopId, bool? isActive, CancellationToken cancellationToken);
    Task<DeviceDto?> FindAsync(Guid deviceId, CancellationToken cancellationToken);
}

public sealed record DeviceCredentials(string ClientId, string ClientSecret, string TokenEndpoint, string Scope)
{
    public override string ToString() => nameof(DeviceCredentials);
}
public sealed record DeviceProvisioningResult(DeviceDto Device, DeviceCredentials Credentials)
{
    public override string ToString() => nameof(DeviceProvisioningResult);
}
public sealed record DeviceMutationResult(Guid Version);
public sealed record DeviceRotationResult(DeviceCredentials Credentials, Guid Version)
{
    public override string ToString() => nameof(DeviceRotationResult);
}
public sealed record DeviceTokenGrant(Guid DeviceId, string ClientId, string WorkshopId,
    Guid AuthorizationVersion, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt);

public interface IDeviceCredentialService
{
    // Implement with the OAuth manager in the same scoped context/transaction as ILprUnitOfWork.
    Task<DeviceCredentials> CreateAsync(Guid deviceId, CancellationToken cancellationToken);
    Task<DeviceCredentials> RotateAsync(Guid deviceId, CancellationToken cancellationToken);
    Task RevokeAsync(Guid deviceId, CancellationToken cancellationToken);
    Task<bool> VerifyAsync(string clientId, string clientSecret, CancellationToken cancellationToken);
}

public sealed record LprReceipt(Guid EventId, DateTimeOffset ReceivedAt, string PlateStatus);
public sealed record ReceiveLprEventResult(bool Duplicate, LprReceipt Receipt);

// Only a confirmed rollback of a collision on the global event key may produce this signal.
public sealed class LprEventIdentityCollisionException(Exception inner)
    : Exception("The LPR event identity already exists.", inner);

public sealed record LprTransactionScope(Guid? AdministratorId, Guid DeviceId, Guid? EventId = null);
public interface ILprUnitOfWork
{
    // Catalog -> administrator (when present) -> device -> event. Commit is the success boundary.
    // No external I/O or blind write retries. The callback shares the OAuth stores' context.
    Task<T> ExecuteAsync<T>(LprTransactionScope scope, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken);
}
public interface ILprClock
{
    // A shared UTC reading; within a use case obtain once inside the transaction.
    Task<DateTimeOffset> GetUtcNowAsync(CancellationToken cancellationToken);
}
public interface ILprRateLimitStore
{
    // Separate lpr namespaces; invoke outside business locks, including for duplicates.
    Task<RateDecision> AdmitAsync(RatePolicy policy, string subject, CancellationToken cancellationToken);
}
