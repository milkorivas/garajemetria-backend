namespace GarajeMetria.Domain.Lpr;

public enum DeviceAdministrationOperation
{
    Created,
    Renamed,
    Deactivated,
    Reactivated,
    CredentialsRotated,
    CredentialsRevoked,
    LocationChanged
}

public sealed record DeviceAdministrationSnapshot(Guid DeviceId, string WorkshopId, string Name,
    bool IsActive, bool HasCredentials, Guid Version, Guid AuthorizationVersion,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public Guid? LocationId { get; init; }
    public string? LocationDescription { get; init; }
    public DateTimeOffset? LocationRecordedAt { get; init; }
    public override string ToString() => nameof(DeviceAdministrationSnapshot);
}

public sealed class DeviceAdministrationAuditEntry
{
    public DeviceAdministrationAuditEntry(Guid id, Guid actorId, DeviceAdministrationOperation operation,
        DeviceAdministrationSnapshot? previous, DeviceAdministrationSnapshot current,
        DateTimeOffset occurredAt, string correlationId)
    {
        ArgumentNullException.ThrowIfNull(current);
        ValidateLocation(current);
        if (previous is not null) ValidateLocation(previous);
        if (id == Guid.Empty || actorId == Guid.Empty || !Enum.IsDefined(operation) ||
            current.DeviceId == Guid.Empty || current.Version == Guid.Empty || current.AuthorizationVersion == Guid.Empty ||
            (operation == DeviceAdministrationOperation.Created) != (previous is null) ||
            previous is not null && (previous.DeviceId != current.DeviceId || previous.WorkshopId != current.WorkshopId ||
                previous.Version == current.Version || previous.CreatedAt != current.CreatedAt))
            throw new ArgumentException("Invalid device administration audit.");
        if (!IsConsistent(operation, previous, current))
            throw new ArgumentException("The audit operation does not match the device transition.");
        LprDevice.RequireUtc(occurredAt);
        LprDevice.RequireUtc(current.CreatedAt);
        LprDevice.RequireUtc(current.UpdatedAt);
        LprText.Require(current.Name, 128);
        LprText.Require(current.WorkshopId, 256);
        LprText.Require(correlationId, 128);
        if (correlationId != correlationId.Trim() || current.UpdatedAt != occurredAt)
            throw new ArgumentException("Invalid audit correlation or instant.");
        Id = id;
        ActorId = actorId;
        Operation = operation;
        Previous = previous;
        Current = current;
        OccurredAt = occurredAt;
        CorrelationId = correlationId;
    }

    public Guid Id { get; }
    public Guid ActorId { get; }
    public Guid DeviceId => Current.DeviceId;
    public DeviceAdministrationOperation Operation { get; }
    public DeviceAdministrationSnapshot? Previous { get; }
    public DeviceAdministrationSnapshot Current { get; }
    public DateTimeOffset OccurredAt { get; }
    public string CorrelationId { get; }

    private static bool IsConsistent(DeviceAdministrationOperation operation,
        DeviceAdministrationSnapshot? previous, DeviceAdministrationSnapshot current)
    {
        if (previous is null) return current.IsActive && current.HasCredentials && current.CreatedAt == current.UpdatedAt;
        var sameName = previous.Name == current.Name;
        var sameActivity = previous.IsActive == current.IsActive;
        var sameCredentials = previous.HasCredentials == current.HasCredentials;
        var revoked = previous.AuthorizationVersion != current.AuthorizationVersion;
        var sameLocation = previous.LocationId == current.LocationId &&
            previous.LocationDescription == current.LocationDescription && previous.LocationRecordedAt == current.LocationRecordedAt;
        if (operation != DeviceAdministrationOperation.LocationChanged && !sameLocation) return false;
        return operation switch
        {
            DeviceAdministrationOperation.Renamed => !sameName && sameActivity && sameCredentials && !revoked,
            DeviceAdministrationOperation.Deactivated => sameName && previous.IsActive && !current.IsActive && !current.HasCredentials && revoked,
            DeviceAdministrationOperation.Reactivated => sameName && !previous.IsActive && current.IsActive && current.HasCredentials && revoked,
            DeviceAdministrationOperation.CredentialsRotated => sameName && previous.IsActive && current.IsActive && current.HasCredentials && revoked,
            DeviceAdministrationOperation.CredentialsRevoked => sameName && sameActivity && previous.HasCredentials && !current.HasCredentials && revoked,
            DeviceAdministrationOperation.LocationChanged => sameName && sameActivity && sameCredentials && !revoked &&
                current.LocationId is not null && previous.LocationId != current.LocationId &&
                previous.LocationDescription != current.LocationDescription && current.LocationRecordedAt == current.UpdatedAt,
            _ => false
        };
    }

    private static void ValidateLocation(DeviceAdministrationSnapshot snapshot)
    {
        if (snapshot.LocationId is null)
        {
            if (snapshot.LocationDescription is not null || snapshot.LocationRecordedAt is not null)
                throw new ArgumentException("Unknown location cannot have location metadata.");
            return;
        }
        if (snapshot.LocationId == Guid.Empty || snapshot.LocationRecordedAt is null)
            throw new ArgumentException("Invalid location audit snapshot.");
        LprText.Require(snapshot.LocationDescription!, 256);
        LprDevice.RequireUtc(snapshot.LocationRecordedAt.Value);
    }
}
