namespace GarajeMetria.Domain.Lpr;

public sealed record LprDeviceLocation
{
    public LprDeviceLocation(Guid id, Guid deviceId, string workshopId, string description, DateTimeOffset recordedAt, long creationOrder)
    {
        ArgumentNullException.ThrowIfNull(description);
        if (id == Guid.Empty || deviceId == Guid.Empty || creationOrder <= 0 || description.Any(char.IsControl))
            throw new ArgumentException("Invalid device location.");
        LprText.Require(workshopId, 256);
        LprText.Require(description.Trim(), 256);
        LprDevice.RequireUtc(recordedAt);
        Id = id; DeviceId = deviceId; WorkshopId = workshopId; Description = description.Trim();
        RecordedAt = recordedAt; CreationOrder = creationOrder;
    }
    public Guid Id { get; }
    public Guid DeviceId { get; }
    public string WorkshopId { get; }
    public string Description { get; }
    public DateTimeOffset RecordedAt { get; }
    public long CreationOrder { get; }
    public override string ToString() => nameof(LprDeviceLocation);
}
