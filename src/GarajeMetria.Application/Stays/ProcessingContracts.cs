using GarajeMetria.Domain.Stays;

namespace GarajeMetria.Application.Stays;

public sealed record StayProcessingLimits(int PageSize, int ObservationLimit, int StayLimit);
public sealed record StayRecalculationResult(long InputRevision, long ProjectionRevision, bool Changed);
public interface IStayRecalculationService
{
    // Internal orchestration, within the caller's validated/authorized partition transaction. Never commits.
    Task<StayRecalculationResult> RecalculateAsync(StayPartition partition, DateTimeOffset seed, DateTimeOffset now,
        StayProjectionCause cause, StayObservation? pendingObservation, CancellationToken cancellationToken);
}
public interface IObservationLocationWriter
{
    Task CaptureAsync(StayObservation observation, CancellationToken cancellationToken);
}
public interface IStayProcessingObserver
{
    void Recalculated(StayProjectionCause cause, int readings, int created, int updated, int absorbed, int estimated, TimeSpan elapsed);
}
