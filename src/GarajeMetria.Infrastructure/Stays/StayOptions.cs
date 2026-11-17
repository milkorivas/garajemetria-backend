using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GarajeMetria.Domain.Stays;

namespace GarajeMetria.Infrastructure.Stays;

public sealed record StayOptions
{
    public int EstimatedDurationMinutes { get; init; } = 30;
    public int RepeatWindowSeconds { get; init; } = 60;
    public int MaximumAssociationHours { get; init; } = 48;
    public int ClosurePollSeconds { get; init; } = 30;
    public int ClosureBatchSize { get; init; } = 100;
    public int ObservationPageSize { get; init; } = 500;
    public int ContextObservationLimit { get; init; } = 20000;
    public int ContextStayLimit { get; init; } = 2000;
    public const string TimeZoneId = "Atlantic/Canary";
    public const int AlgorithmVersion = 1;

    public TimeSpan EstimatedDuration => TimeSpan.FromTicks(checked((long)EstimatedDurationMinutes * TimeSpan.TicksPerMinute));
    public TimeSpan RepeatWindow => TimeSpan.FromTicks(checked((long)RepeatWindowSeconds * TimeSpan.TicksPerSecond));
    public TimeSpan MaximumAssociationDuration => TimeSpan.FromTicks(checked((long)MaximumAssociationHours * TimeSpan.TicksPerHour));

    public void Validate()
    {
        if (EstimatedDurationMinutes <= 0 || RepeatWindowSeconds <= 0 || MaximumAssociationHours <= 0 ||
            ClosurePollSeconds <= 0 || (long)ClosurePollSeconds * 1000 > uint.MaxValue - 1L ||
            ClosureBatchSize is < 1 or > 100 || ObservationPageSize is < 1 or > 1000 || ContextObservationLimit <= 0 || ContextStayLimit <= 0)
            throw new InvalidOperationException("Invalid Stays configuration.");
        try
        {
            _ = EstimatedDuration;
            _ = RepeatWindow;
            _ = MaximumAssociationDuration;
        }
        catch (OverflowException ex) { throw new InvalidOperationException("Invalid Stays duration configuration.", ex); }
    }

    public StayPolicySnapshot CreateSnapshot(Guid id) =>
        new(id, EstimatedDuration, RepeatWindow, MaximumAssociationDuration, TimeZoneId, AlgorithmVersion);

    internal bool Matches(StayPolicySnapshot policy) => policy.EstimatedDuration == EstimatedDuration &&
        policy.RepeatWindow == RepeatWindow && policy.MaximumAssociationDuration == MaximumAssociationDuration &&
        policy.TimeZoneId == TimeZoneId && policy.AlgorithmVersion == AlgorithmVersion;

    internal static string Fingerprint(StayPolicySnapshot policy)
    {
        var canonical = string.Create(CultureInfo.InvariantCulture,
            $"garajemetria-stays:v1|{policy.EstimatedDuration.Ticks}|{policy.RepeatWindow.Ticks}|{policy.MaximumAssociationDuration.Ticks}|{policy.TimeZoneId}|{policy.AlgorithmVersion}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
