using GarajeMetria.Domain.Lpr;

namespace GarajeMetria.Domain.Stays;

public enum StayState { Open, PendingEntry, ClosedProvisional }

public sealed class VehicleStay
{
    public VehicleStay(Guid id, StayObservation origin, DateTimeOffset createdAt, long creationOrder,
        StayPolicySnapshot policy, StayBoundary? entry, StayBoundary? exit)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(policy);
        if (id == Guid.Empty || !origin.IsPlateValid || creationOrder <= 0)
            throw new ArgumentException("Invalid stay identity or origin.");

        Id = id;
        OriginEventId = origin.EventId;
        WorkshopId = origin.WorkshopId;
        PlateNormalized = origin.PlateNormalized;
        CreatedAt = UpdatedAt = createdAt.ToUniversalTime();
        CreationOrder = creationOrder;
        Policy = policy;
        ValidateBoundaries(entry, exit);
        Entry = entry;
        Exit = exit;
    }

    public Guid Id { get; }
    public Guid OriginEventId { get; }
    public string WorkshopId { get; }
    public string PlateNormalized { get; }
    public DateTimeOffset CreatedAt { get; }
    public long CreationOrder { get; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public StayPolicySnapshot Policy { get; }
    public StayBoundary? Entry { get; private set; }
    public StayBoundary? Exit { get; private set; }
    public DateTimeOffset? EntryAt => Entry?.At;
    public DateTimeOffset? ExitAt => Exit?.At;
    public StayState State => Entry is null ? StayState.PendingEntry :
        Exit is null ? StayState.Open : StayState.ClosedProvisional;
    public TimeSpan? Duration => Entry is not null && Exit is not null ? Exit.At - Entry.At : null;

    // Time is supplied by the caller; no ticking duration or system clock is stored in the entity.
    public TimeSpan? GetDurationAt(DateTimeOffset instant) => State switch
    {
        StayState.ClosedProvisional => Duration,
        StayState.Open when instant >= Entry!.At => instant - Entry.At,
        _ => null
    };

    public bool ReviseBoundaries(StayBoundary? entry, StayBoundary? exit, DateTimeOffset revisedAt)
    {
        ValidateBoundaries(entry, exit);
        if (revisedAt < UpdatedAt)
            throw new ArgumentException("A stay revision cannot precede its last update.");
        if (Entry == entry && Exit == exit) return false;

        Entry = entry;
        Exit = exit;
        UpdatedAt = revisedAt.ToUniversalTime();
        return true;
    }

    private void ValidateBoundaries(StayBoundary? entry, StayBoundary? exit)
    {
        if (entry is null && exit is null)
            throw new ArgumentException("A stay requires at least one boundary.");
        ValidateBoundary(entry, LprDirection.Entry);
        ValidateBoundary(exit, LprDirection.Exit);
        if (entry is not null && exit is not null && exit.At < entry.At)
            throw new ArgumentException("A stay exit cannot precede its entry.");
        var hasEstimate = entry?.Source == StayBoundarySource.Estimated || exit?.Source == StayBoundarySource.Estimated;
        if (hasEstimate && (entry is null || exit is null ||
            entry.Source == StayBoundarySource.Estimated && exit.Source == StayBoundarySource.Estimated ||
            exit.At - entry.At != Policy.EstimatedDuration))
            throw new ArgumentException("An estimated boundary requires an observed counterpart and the historical duration.");
    }

    private void ValidateBoundary(StayBoundary? boundary, LprDirection role)
    {
        if (boundary?.Observation is not { } observation) return;
        if (!string.Equals(observation.WorkshopId, WorkshopId, StringComparison.Ordinal) ||
            !string.Equals(observation.PlateNormalized, PlateNormalized, StringComparison.Ordinal) ||
            boundary.Source == StayBoundarySource.Explicit && observation.Direction != role)
            throw new ArgumentException("A stay boundary must match its vehicle, workshop and declared movement.");
    }

    public override string ToString() => nameof(VehicleStay);
}
