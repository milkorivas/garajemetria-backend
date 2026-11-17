using GarajeMetria.Domain.Lpr;

namespace GarajeMetria.Domain.Tests;

public sealed class DeviceLocationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);
    [Fact]
    public void LocationVersionsPreserveDeviceAuthorizationAndHistoricalSnapshots()
    {
        var device = new LprDevice(Guid.NewGuid(), "Camera", "site", true, Now);
        var authorization = device.AuthorizationVersion;
        Assert.True(device.ChangeLocation(" Access A ", Now));
        var first = device.Snapshot(); var oldVersion = device.Version;
        Assert.Equal("Access A", first.LocationDescription);
        Assert.False(device.ChangeLocation("Access A", Now.AddMinutes(1)));
        Assert.Equal(oldVersion, device.Version);
        Assert.True(device.ChangeLocation("Access B", Now.AddMinutes(2)));
        Assert.Equal(authorization, device.AuthorizationVersion);
        Assert.Equal("Access A", first.LocationDescription);
        Assert.NotEqual(first.LocationId, device.Location!.Id);
        Assert.Equal(2, device.Location.CreationOrder);
        var audit = new DeviceAdministrationAuditEntry(Guid.NewGuid(), Guid.NewGuid(), DeviceAdministrationOperation.LocationChanged,
            first, device.Snapshot(), Now.AddMinutes(2), "synthetic");
        Assert.Equal(first, audit.Previous);
        Assert.DoesNotContain("Access B", device.Location.ToString());
        Assert.DoesNotContain("Access A", first.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Access\n")]
    public void InvalidDescriptionsCannotMutateTheDevice(string value)
    {
        var device = new LprDevice(Guid.NewGuid(), "Camera", "site", true, Now); var before = device.Snapshot();
        Assert.Throws<ArgumentException>(() => device.ChangeLocation(value, Now));
        Assert.Equal(before, device.Snapshot()); Assert.Null(device.CurrentLocationId);
    }

    [Fact]
    public void ForeignRestorationOrACombinedCredentialChangeCannotBeAuditedAsLocationOnly()
    {
        var device = new LprDevice(Guid.NewGuid(), "Camera", "site", true, Now);
        device.ChangeLocation("Access A", Now); var before = device.Snapshot();
        Assert.Throws<ArgumentException>(() => device.RestoreLocation(new(device.Location!.Id, Guid.NewGuid(), "site", "Access A", Now, 1)));
        Assert.Throws<ArgumentException>(() => device.RestoreLocation(null));
        device.ChangeLocation("Access B", Now.AddMinutes(1)); device.RevokeCredentials(Now.AddMinutes(1));
        Assert.Throws<ArgumentException>(() => new DeviceAdministrationAuditEntry(Guid.NewGuid(), Guid.NewGuid(),
            DeviceAdministrationOperation.LocationChanged, before, device.Snapshot(), Now.AddMinutes(1), "synthetic"));
    }
}
