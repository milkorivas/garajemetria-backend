using GarajeMetria.Domain.Lpr;

namespace GarajeMetria.Domain.Stays;

// Classification is supplied from the saved observation, never recomputed by the stay model.
public sealed record StayObservation
{
    public StayObservation(Guid eventId, Guid deviceId, string workshopId, string plateNormalized,
        DateTimeOffset capturedAt, DateTimeOffset receivedAt, bool isPlateValid,
        string plateRulesVersion, decimal confidence, LprDirection? direction)
    {
        if (!LprEventRules.ValidEventId(eventId) || deviceId == Guid.Empty ||
            !LprEventRules.ValidText(workshopId, 256) || !LprEventRules.ValidText(plateNormalized, 32) ||
            !LprEventRules.ValidText(plateRulesVersion, 32) || !LprEventRules.ValidConfidence(confidence) ||
            direction is { } d && !Enum.IsDefined(d))
            throw new ArgumentException("Invalid stay observation.");

        EventId = eventId;
        DeviceId = deviceId;
        WorkshopId = workshopId;
        PlateNormalized = plateNormalized;
        CapturedAt = capturedAt.ToUniversalTime();
        ReceivedAt = receivedAt.ToUniversalTime();
        IsPlateValid = isPlateValid;
        PlateRulesVersion = plateRulesVersion;
        Confidence = confidence;
        Direction = direction;
    }

    public Guid EventId { get; }
    public Guid DeviceId { get; }
    public string WorkshopId { get; }
    public string PlateNormalized { get; }
    public DateTimeOffset CapturedAt { get; }
    public DateTimeOffset ReceivedAt { get; }
    public bool IsPlateValid { get; }
    public string PlateRulesVersion { get; }
    public decimal Confidence { get; }
    public LprDirection? Direction { get; }

    public static StayObservation FromEvent(LprEvent observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return new(observation.EventId, observation.DeviceId, observation.WorkshopId,
            observation.PlateNormalized, observation.CapturedAt, observation.ReceivedAt,
            observation.PlateStatus == "valid", observation.PlateRulesVersion,
            observation.Confidence, observation.Direction);
    }

    public override string ToString() => nameof(StayObservation);
}
