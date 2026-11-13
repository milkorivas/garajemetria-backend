namespace GarajeMetria.Domain.Lpr;

public enum LprDirection { Entry, Exit }

public sealed record LprObservation(Guid EventId, Guid DeviceId, string WorkshopId,
    DateTimeOffset CapturedAt, string PlateOriginal, decimal Confidence, LprDirection? Direction)
{
    public bool Matches(LprObservation other) => EventId == other.EventId && DeviceId == other.DeviceId &&
        string.Equals(WorkshopId, other.WorkshopId, StringComparison.Ordinal) && CapturedAt == other.CapturedAt &&
        string.Equals(PlateOriginal, other.PlateOriginal, StringComparison.Ordinal) && Confidence == other.Confidence && Direction == other.Direction;
    public override string ToString() => nameof(LprObservation);
}

public static class LprEventRules
{
    public static bool ValidEventId(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes, bigEndian: true, out _);
        return (bytes[8] & 0xc0) == 0x80 && (bytes[6] >> 4) is >= 1 and <= 8;
    }

    public static bool ValidConfidence(decimal confidence) =>
        confidence is >= 0 and <= 1 && decimal.Round(confidence, 8) == confidence;

    public static bool ValidPlateText(string? value) => ValidText(value, 32) &&
        value!.Any(c => !char.IsWhiteSpace(c) && c != '-');

    public static bool ValidText(string? value, int maximum) => LprText.IsValid(value, maximum);
}

// A decision for a new observation only; persisted duplicates bypass this window.
public sealed class LprCaptureWindow
{
    public LprCaptureWindow(TimeSpan maximumAge, TimeSpan maximumFuture)
    {
        if (maximumAge < TimeSpan.Zero || maximumFuture < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumAge), "Capture tolerances cannot be negative.");
        MaximumAge = maximumAge;
        MaximumFuture = maximumFuture;
    }

    public TimeSpan MaximumAge { get; }
    public TimeSpan MaximumFuture { get; }

    public bool Contains(DateTimeOffset capturedAt, DateTimeOffset receivedAt)
    {
        var difference = capturedAt - receivedAt;
        return difference >= -MaximumAge && difference <= MaximumFuture;
    }
}

public sealed class LprEvent
{
    public LprEvent(Guid eventId, Guid deviceId, string workshopId, DateTimeOffset capturedAt,
        DateTimeOffset receivedAt, string plateOriginal, decimal confidence, LprDirection? direction)
    {
        if (!LprEventRules.ValidEventId(eventId) || deviceId == Guid.Empty ||
            !LprEventRules.ValidConfidence(confidence) || direction is { } d && !Enum.IsDefined(d))
            throw new ArgumentException("Invalid LPR event data.");
        LprText.Require(workshopId, 256);
        var plate = SpanishPlateClassifier.Classify(plateOriginal);
        EventId = eventId;
        DeviceId = deviceId;
        WorkshopId = workshopId;
        CapturedAt = capturedAt.ToUniversalTime();
        ReceivedAt = receivedAt.ToUniversalTime();
        PlateOriginal = plate.Original;
        PlateNormalized = plate.Normalized;
        PlateStatus = plate.IsValid ? "valid" : "invalid";
        PlateFormat = plate.Format;
        PlateRulesVersion = plate.RulesVersion;
        Confidence = confidence;
        Direction = direction;
    }

    public Guid EventId { get; }
    public Guid DeviceId { get; }
    public string WorkshopId { get; }
    public DateTimeOffset CapturedAt { get; }
    public DateTimeOffset ReceivedAt { get; }
    public string PlateOriginal { get; }
    public string PlateNormalized { get; }
    public string PlateStatus { get; }
    public string? PlateFormat { get; }
    public string PlateRulesVersion { get; }
    public decimal Confidence { get; }
    public LprDirection? Direction { get; }

    // Compare incoming values directly: retries must not reclassify the saved observation.
    public bool Matches(Guid eventId, Guid deviceId, string workshopId, DateTimeOffset capturedAt,
        string plateOriginal, decimal confidence, LprDirection? direction) =>
        new LprObservation(EventId, DeviceId, WorkshopId, CapturedAt, PlateOriginal, Confidence, Direction)
            .Matches(new(eventId, deviceId, workshopId, capturedAt, plateOriginal, confidence, direction));

    public override string ToString() => nameof(LprEvent);
}
