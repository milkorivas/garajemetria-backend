using GarajeMetria.Domain.Lpr;

namespace GarajeMetria.Application.Stays;

// Internal read port. RF-005 must resolve the trusted workshop/device resource scope before calling it.
// Invalid readings have no fabricated stay partition and remain discoverable through this separate port.
public sealed record ObservationAuditWindow
{
    public ObservationAuditWindow(Guid deviceId, string workshopId, DateTimeOffset start, DateTimeOffset end,
        int pageSize, string? plateOriginal = null)
    {
        if (deviceId == Guid.Empty || !LprEventRules.ValidText(workshopId, 256) || start >= end || pageSize is < 1 or > 1000 ||
            plateOriginal is not null && !LprEventRules.ValidText(plateOriginal, 32)) throw new ArgumentException("Invalid observation audit window.");
        DeviceId = deviceId; WorkshopId = workshopId; Start = start.ToUniversalTime(); End = end.ToUniversalTime();
        PageSize = pageSize; PlateOriginal = plateOriginal;
    }
    public Guid DeviceId { get; }
    public string WorkshopId { get; }
    public DateTimeOffset Start { get; }
    public DateTimeOffset End { get; }
    public int PageSize { get; }
    public string? PlateOriginal { get; }
    public override string ToString() => nameof(ObservationAuditWindow);
}
public sealed record StoredObservationAudit(Guid EventId, Guid DeviceId, string WorkshopId, DateTimeOffset CapturedAt,
    DateTimeOffset ReceivedAt, string PlateOriginal, string PlateNormalized, string PlateStatus, string? PlateFormat,
    string PlateRulesVersion, decimal Confidence, LprDirection? Direction, ObservationLocationDto Location)
{
    public override string ToString() => nameof(StoredObservationAudit);
}
public sealed record ObservationAuditPage(IReadOnlyList<StoredObservationAudit> Items, StayObservationCursor? NextCursor);
public interface IObservationAuditQuery
{
    Task<ObservationAuditPage> ReadAsync(ObservationAuditWindow window, StayObservationCursor? after, CancellationToken cancellationToken);
}
