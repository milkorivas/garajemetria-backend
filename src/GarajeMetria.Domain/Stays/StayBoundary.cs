namespace GarajeMetria.Domain.Stays;

public enum StayBoundarySource { Explicit, Inferred, Estimated }

public sealed record StayBoundary
{
    private StayBoundary(DateTimeOffset at, StayBoundarySource source, StayObservation? observation)
    {
        At = at.ToUniversalTime();
        Source = source;
        Observation = observation;
    }

    public DateTimeOffset At { get; }
    public StayBoundarySource Source { get; }
    public StayObservation? Observation { get; }
    public Guid? EventId => Observation?.EventId;

    public static StayBoundary Observed(StayObservation observation, StayBoundarySource source)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!observation.IsPlateValid || source is not (StayBoundarySource.Explicit or StayBoundarySource.Inferred))
            throw new ArgumentException("An observed stay boundary requires a valid observation and source.");
        return new(observation.CapturedAt, source, observation);
    }

    public static StayBoundary Estimated(DateTimeOffset at) => new(at, StayBoundarySource.Estimated, null);
    public override string ToString() => nameof(StayBoundary);
}
