namespace GarajeMetria.Domain.Stays;

public sealed record StayPolicySnapshot
{
    public StayPolicySnapshot(Guid id, TimeSpan estimatedDuration, TimeSpan repeatWindow,
        TimeSpan maximumAssociationDuration, string timeZoneId, int algorithmVersion)
    {
        if (id == Guid.Empty || estimatedDuration <= TimeSpan.Zero || repeatWindow <= TimeSpan.Zero ||
            maximumAssociationDuration <= TimeSpan.Zero || timeZoneId != "Atlantic/Canary" || algorithmVersion <= 0)
            throw new ArgumentException("Invalid stay policy snapshot.");

        Id = id;
        EstimatedDuration = estimatedDuration;
        RepeatWindow = repeatWindow;
        MaximumAssociationDuration = maximumAssociationDuration;
        TimeZoneId = timeZoneId;
        AlgorithmVersion = algorithmVersion;
    }

    public Guid Id { get; }
    public TimeSpan EstimatedDuration { get; }
    public TimeSpan RepeatWindow { get; }
    public TimeSpan MaximumAssociationDuration { get; }
    public string TimeZoneId { get; }
    public int AlgorithmVersion { get; }
}
