namespace GarajeMetria.Domain.Stays;

public sealed class StayObservationOrder : IComparer<StayObservation>
{
    public static StayObservationOrder Instance { get; } = new();

    private StayObservationOrder() { }

    public int Compare(StayObservation? x, StayObservation? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        var captureOrder = x.CapturedAt.CompareTo(y.CapturedAt);
        if (captureOrder != 0) return captureOrder;

        Span<byte> left = stackalloc byte[16];
        Span<byte> right = stackalloc byte[16];
        x.EventId.TryWriteBytes(left, bigEndian: true, out _);
        y.EventId.TryWriteBytes(right, bigEndian: true, out _);
        return left.SequenceCompareTo(right);
    }
}
