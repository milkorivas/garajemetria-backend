using GarajeMetria.Application.Stays;
using GarajeMetria.Domain.Stays;

namespace GarajeMetria.Infrastructure.Persistence;

// Persistence-only rows. Events remain the source of readings; these rows store recalculable projections.
public sealed class StayPolicyRow
{
    public Guid Id { get; set; }
    public string Fingerprint { get; set; } = "";
    public long EstimatedDurationTicks { get; set; }
    public long RepeatWindowTicks { get; set; }
    public long MaximumAssociationTicks { get; set; }
    public string TimeZoneId { get; set; } = "";
    public int AlgorithmVersion { get; set; }
}

public sealed class StayConfigurationRow
{
    public int Id { get; set; }
    public Guid ActivePolicyId { get; set; }
    public Guid Version { get; set; }
    public DateTimeOffset ActivatedAt { get; set; }
}

public sealed class VehicleStayPartitionRow
{
    public Guid Id { get; set; }
    public string WorkshopId { get; set; } = "";
    public string PlateNormalized { get; set; } = "";
    public long InputRevision { get; set; }
    public long ProjectionRevision { get; set; }
    public long LastCreationOrder { get; set; }
    public DateTimeOffset? NextClosureAt { get; set; }
    public override string ToString() => nameof(VehicleStayPartitionRow);
}

public sealed class VehicleStayRow
{
    public Guid Id { get; set; }
    public Guid PartitionId { get; set; }
    public string WorkshopId { get; set; } = "";
    public string PlateNormalized { get; set; } = "";
    public Guid PolicyId { get; set; }
    public Guid OriginEventId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long CreationOrder { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public StayState State { get; set; }
    public DateTimeOffset? EntryAt { get; set; }
    public StayBoundarySource? EntrySource { get; set; }
    public Guid? EntryEventId { get; set; }
    public DateTimeOffset? ExitAt { get; set; }
    public StayBoundarySource? ExitSource { get; set; }
    public Guid? ExitEventId { get; set; }
    public long Revision { get; set; }
    public Guid? AbsorbedByStayId { get; set; }
    public DateTimeOffset? NextClosureAt { get; set; }
    public override string ToString() => nameof(VehicleStayRow);
}

public sealed class StayProjectionRevisionRow
{
    public Guid PartitionId { get; set; }
    public string WorkshopId { get; set; } = "";
    public string PlateNormalized { get; set; } = "";
    public long Revision { get; set; }
    public long InputRevision { get; set; }
    public StayProjectionCause Cause { get; set; }
    public DateTimeOffset EvaluatedAt { get; set; }
    public Guid? CausingEventId { get; set; }
    // Versioned delta document, never the original full LPR payload or a second event store.
    public string ChangesJson { get; set; } = "";
    public int FormatVersion { get; set; }
    public override string ToString() => nameof(StayProjectionRevisionRow);
}

public sealed class StayLineageRow
{
    public Guid PartitionId { get; set; }
    public long Revision { get; set; }
    public Guid SourceStayId { get; set; }
    public Guid TargetStayId { get; set; }
    public StayLineageKind Kind { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

public enum StayObservationRole { Entry, Exit, Intermediate, Repeated, Unassociated }
public sealed class StayObservationAssignmentRow
{
    public Guid EventId { get; set; }
    public Guid PartitionId { get; set; }
    public string WorkshopId { get; set; } = "";
    public string PlateNormalized { get; set; } = "";
    public Guid? StayId { get; set; }
    public Guid? GroupId { get; set; }
    public Guid? GroupOriginEventId { get; set; }
    public Guid? MatchedMovementEventId { get; set; }
    public StayObservationRole Role { get; set; }
    public string Reason { get; set; } = "";
    public long Revision { get; set; }
    public override string ToString() => nameof(StayObservationAssignmentRow);
}

public sealed class StayDayClosureRow
{
    public Guid PartitionId { get; set; }
    public DateOnly LocalDate { get; set; }
    public long ProcessedInputRevision { get; set; }
    public DateTimeOffset ClosedAt { get; set; }
    public DateTimeOffset? NextCheckAt { get; set; }
}

public sealed class LprDeviceLocationRow
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public string WorkshopId { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTimeOffset RecordedAt { get; set; }
    public long CreationOrder { get; set; }
    public override string ToString() => nameof(LprDeviceLocationRow);
}

public sealed class LprObservationLocationRow
{
    public Guid EventId { get; set; }
    public Guid DeviceId { get; set; }
    public string WorkshopId { get; set; } = "";
    public Guid? LocationId { get; set; }
    public ObservationLocationProvenance Provenance { get; set; }
    public override string ToString() => nameof(LprObservationLocationRow);
}
