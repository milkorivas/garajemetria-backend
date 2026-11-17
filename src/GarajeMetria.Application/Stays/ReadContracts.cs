using GarajeMetria.Domain.Lpr;
using GarajeMetria.Domain.Stays;

namespace GarajeMetria.Application.Stays;

public interface IWorkshopCalendar
{
    // Atlantic/Canary only. Implement timezone conversion outside Application; never assume 24-hour days.
    DateOnly GetLocalDate(DateTimeOffset instant);
    StayDayWindow GetDay(DateOnly localDate);
}

public sealed record StayPartition(Guid Id, string WorkshopId, string PlateNormalized, long InputRevision,
    long ProjectionRevision, long LastCreationOrder, DateTimeOffset? NextClosureAt)
{
    public override string ToString() => nameof(StayPartition);
}

// EventId is ordered by canonical network UUID bytes, not provider-native GUID ordering.
public sealed record StayObservationCursor(DateTimeOffset CapturedAt, Guid EventId);
public enum StayReadDirection { Forward, Backward }

public sealed record StayReadWindow
{
    public StayReadWindow(StayPartition partition, DateTimeOffset start, DateTimeOffset end,
        int pageSize, StayReadDirection direction = StayReadDirection.Forward)
    {
        ArgumentNullException.ThrowIfNull(partition);
        if (partition.Id == Guid.Empty || !LprEventRules.ValidText(partition.WorkshopId, 256) ||
            !LprEventRules.ValidText(partition.PlateNormalized, 32) || start >= end ||
            pageSize is < 1 or > 1000 || !Enum.IsDefined(direction))
            throw new ArgumentException("Invalid stay read window.");
        Partition = partition;
        Start = start.ToUniversalTime();
        End = end.ToUniversalTime();
        PageSize = pageSize;
        Direction = direction;
    }

    public StayPartition Partition { get; }
    public DateTimeOffset Start { get; }
    public DateTimeOffset End { get; }
    public int PageSize { get; }
    public StayReadDirection Direction { get; }
    public override string ToString() => nameof(StayReadWindow);
}

public enum ObservationLocationProvenance { Unknown, AtCapture, LatestKnownForFutureCapture }
public sealed record ObservationLocationDto(Guid? LocationId, string? Description, DateTimeOffset? RecordedAt,
    ObservationLocationProvenance Provenance)
{
    public override string ToString() => nameof(ObservationLocationDto);
}
public sealed record StayObservationDto(StayObservation Observation, ObservationLocationDto Location)
{
    public override string ToString() => nameof(StayObservationDto);
}
public sealed record StayObservationPage(IReadOnlyList<StayObservationDto> Items, StayObservationCursor? NextCursor);

public interface IStayObservationQuery
{
    // Materialized [Start,End) page in requested direction; exclusive cursor, at most PageSize items.
    // Include every saved reading in the partition (even repeated/unassigned); never reclassify/filter confidence.
    // NextCursor is null only when exhausted. Caller carries groups across pages and expands both borders.
    // Internal port: caller must establish trusted workshop/device access or the internal closure scope first.
    Task<StayObservationPage> ReadAsync(StayReadWindow window, StayObservationCursor? after,
        CancellationToken cancellationToken);
    // Explicit adjacency is not capped by M: a chain of observed exits can extend a visit across a long gap.
    Task<StayObservation?> ReadAdjacentExplicitAsync(StayPartition partition, DateTimeOffset point,
        StayReadDirection direction, CancellationToken cancellationToken);
}
