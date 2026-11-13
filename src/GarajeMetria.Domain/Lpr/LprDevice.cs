namespace GarajeMetria.Domain.Lpr;

public sealed class LprDevice
{
    private LprDevice() { }

    public LprDevice(Guid id, string name, string workshopId, bool workshopIsActive, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty) throw new ArgumentException("A device identifier is required.", nameof(id));
        LprText.Require(workshopId, 256);
        RequireActiveWorkshop(workshopIsActive);
        RequireUtc(createdAt);
        Id = id;
        Name = NormalizeName(name);
        WorkshopId = workshopId;
        IsActive = true;
        HasCredentials = true;
        Version = Guid.NewGuid();
        AuthorizationVersion = Guid.NewGuid();
        CreatedAt = UpdatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";
    public string WorkshopId { get; private set; } = "";
    public bool IsActive { get; private set; }
    public bool HasCredentials { get; private set; }
    public Guid Version { get; private set; }
    public Guid AuthorizationVersion { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public bool Rename(string name, DateTimeOffset changedAt)
    {
        var normalized = NormalizeName(name);
        RequireUtc(changedAt);
        if (Name == normalized) return false;
        Name = normalized;
        Change(changedAt, false);
        return true;
    }

    public bool Deactivate(DateTimeOffset changedAt)
    {
        RequireUtc(changedAt);
        if (!IsActive) return false;
        IsActive = HasCredentials = false;
        Change(changedAt, true);
        return true;
    }

    public bool RevokeCredentials(DateTimeOffset changedAt)
    {
        RequireUtc(changedAt);
        if (!HasCredentials) return false;
        HasCredentials = false;
        Change(changedAt, true);
        return true;
    }

    public void RotateCredentials(bool workshopIsActive, DateTimeOffset changedAt)
    {
        RequireActiveWorkshop(workshopIsActive);
        RequireUtc(changedAt);
        if (!IsActive) throw new InvalidOperationException("An inactive device cannot rotate credentials.");
        HasCredentials = true;
        Change(changedAt, true);
    }

    public void Reactivate(bool workshopIsActive, DateTimeOffset changedAt)
    {
        RequireActiveWorkshop(workshopIsActive);
        RequireUtc(changedAt);
        if (IsActive) throw new InvalidOperationException("The device is already active.");
        IsActive = HasCredentials = true;
        Change(changedAt, true);
    }

    // Catalog publication invalidates tokens without changing device activity or credentials.
    public void InvalidateAuthorizationForWorkshop(DateTimeOffset changedAt)
    {
        RequireUtc(changedAt);
        Change(changedAt, true);
    }

    public DeviceAdministrationSnapshot Snapshot() => new(Id, WorkshopId, Name, IsActive,
        HasCredentials, Version, AuthorizationVersion, CreatedAt, UpdatedAt);

    private void Change(DateTimeOffset changedAt, bool invalidateAuthorization)
    {
        Version = Guid.NewGuid();
        if (invalidateAuthorization) AuthorizationVersion = Guid.NewGuid();
        UpdatedAt = changedAt;
    }

    private static string NormalizeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var normalized = name.Trim();
        LprText.Require(normalized, 128);
        // Check the original too: trimming must not silently remove control characters.
        if (name.Any(char.IsControl)) throw new ArgumentException("Invalid device name.", nameof(name));
        return normalized;
    }

    private static void RequireActiveWorkshop(bool isActive)
    {
        if (!isActive) throw new InvalidOperationException("An active workshop is required.");
    }

    internal static void RequireUtc(DateTimeOffset instant)
    {
        if (instant.Offset != TimeSpan.Zero) throw new ArgumentException("A UTC instant is required.", nameof(instant));
    }
}

internal static class LprText
{
    internal static void Require(string value, int maximum)
    {
        if (!IsValid(value, maximum)) throw new ArgumentException("Invalid LPR text.", nameof(value));
    }

    internal static bool IsValid(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
            return false;
        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsSurrogate(value[i])) continue;
            if (!char.IsHighSurrogate(value[i]) || i + 1 == value.Length || !char.IsLowSurrogate(value[++i]))
                return false;
        }
        return true;
    }
}
