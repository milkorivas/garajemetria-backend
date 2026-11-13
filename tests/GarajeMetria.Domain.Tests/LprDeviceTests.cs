using GarajeMetria.Domain.Lpr;

namespace GarajeMetria.Domain.Tests;

public sealed class LprDeviceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static LprDevice Create() => new(Guid.NewGuid(), " Camera 1 ", "synthetic-workshop", true, Now);

    [Fact]
    public void CreationSetsIndependentIdentitiesAndImmutableAssociation()
    {
        var first = Create();
        var second = Create();
        Assert.Equal("Camera 1", first.Name);
        Assert.Equal(first.WorkshopId, second.WorkshopId);
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.Version, second.Version);
        Assert.NotEqual(first.AuthorizationVersion, second.AuthorizationVersion);
        Assert.True(first.IsActive);
        Assert.True(first.HasCredentials);
        Assert.Equal(Now, first.CreatedAt);
        Assert.Equal(Now, first.UpdatedAt);
        Assert.False(typeof(LprDevice).GetProperty(nameof(LprDevice.WorkshopId))!.SetMethod!.IsPublic);
    }

    [Fact]
    public void RenamingChangesConcurrencyButPreservesAuthorizationAndNoOpPreservesAllState()
    {
        var device = Create();
        var before = device.Snapshot();
        Assert.True(device.Rename(" Camera 2 ", Now.AddMinutes(1)));
        Assert.Equal("Camera 2", device.Name);
        Assert.NotEqual(before.Version, device.Version);
        Assert.Equal(before.AuthorizationVersion, device.AuthorizationVersion);
        var after = device.Snapshot();
        Assert.False(device.Rename("Camera 2", Now.AddMinutes(2)));
        Assert.Equal(after, device.Snapshot());
    }

    [Fact]
    public void RevocationAndRotationInvalidateAuthorizationWithoutChangingAssociation()
    {
        var device = Create();
        var before = device.Snapshot();
        Assert.True(device.RevokeCredentials(Now.AddMinutes(1)));
        Assert.True(device.IsActive);
        Assert.False(device.HasCredentials);
        AssertVersionsChanged(before, device);
        var revoked = device.Snapshot();
        Assert.False(device.RevokeCredentials(Now.AddMinutes(2)));
        Assert.Equal(revoked, device.Snapshot());
        device.RotateCredentials(true, Now.AddMinutes(3));
        Assert.True(device.HasCredentials);
        AssertVersionsChanged(revoked, device);
        var rotated = device.Snapshot();
        device.RotateCredentials(true, Now.AddMinutes(4));
        AssertVersionsChanged(rotated, device);
        Assert.Equal(before.DeviceId, device.Id);
        Assert.Equal(before.WorkshopId, device.WorkshopId);
    }

    [Fact]
    public void DeactivationAndReactivationNeverRestorePreviousAuthorization()
    {
        var device = Create();
        var active = device.Snapshot();
        Assert.True(device.Deactivate(Now.AddMinutes(1)));
        Assert.False(device.IsActive);
        Assert.False(device.HasCredentials);
        AssertVersionsChanged(active, device);
        var inactive = device.Snapshot();
        Assert.False(device.Deactivate(Now.AddMinutes(2)));
        Assert.False(device.RevokeCredentials(Now.AddMinutes(2)));
        Assert.Equal(inactive, device.Snapshot());
        Assert.Throws<InvalidOperationException>(() => device.RotateCredentials(true, Now.AddMinutes(2)));
        Assert.Throws<InvalidOperationException>(() => device.Reactivate(false, Now.AddMinutes(2)));
        Assert.Equal(inactive, device.Snapshot());
        device.Reactivate(true, Now.AddMinutes(3));
        Assert.True(device.IsActive);
        Assert.True(device.HasCredentials);
        AssertVersionsChanged(inactive, device);
        Assert.NotEqual(active.AuthorizationVersion, device.AuthorizationVersion);
        Assert.Throws<InvalidOperationException>(() => device.Reactivate(true, Now.AddMinutes(4)));
        Assert.Equal(active.WorkshopId, device.WorkshopId);
        Assert.Equal(active.CreatedAt, device.CreatedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorkshopInvalidationPreservesDeviceStateAndCredentials(bool deactivateFirst)
    {
        var device = Create();
        if (deactivateFirst) device.Deactivate(Now);
        var before = device.Snapshot();
        device.InvalidateAuthorizationForWorkshop(Now.AddMinutes(1));
        AssertVersionsChanged(before, device);
        Assert.Equal(before.IsActive, device.IsActive);
        Assert.Equal(before.HasCredentials, device.HasCredentials);
        Assert.Equal(before.WorkshopId, device.WorkshopId);
    }

    [Fact]
    public void InvalidInputsAndInactiveWorkshopsCannotChangeDevice()
    {
        Assert.Throws<ArgumentException>(() => new LprDevice(Guid.Empty, "Camera", "site", true, Now));
        Assert.Throws<InvalidOperationException>(() => new LprDevice(Guid.NewGuid(), "Camera", "site", false, Now));
        Assert.Throws<ArgumentException>(() => new LprDevice(Guid.NewGuid(), "Camera", new string('x', 257), true, Now));
        var device = Create();
        var before = device.Snapshot();
        foreach (var name in new[] { "", "   ", "bad\n", "\ud800", new string('x', 129) })
            Assert.Throws<ArgumentException>(() => device.Rename(name, Now));
        Assert.Throws<InvalidOperationException>(() => device.RotateCredentials(false, Now));
        var nonUtc = Now.ToOffset(TimeSpan.FromHours(1));
        Assert.Throws<ArgumentException>(() => device.Deactivate(nonUtc));
        Assert.Throws<ArgumentException>(() => device.RevokeCredentials(nonUtc));
        Assert.Throws<ArgumentException>(() => device.Rename("other", nonUtc));
        Assert.Throws<ArgumentException>(() => device.RotateCredentials(true, nonUtc));
        Assert.Throws<ArgumentException>(() => device.InvalidateAuthorizationForWorkshop(nonUtc));
        Assert.Equal(before, device.Snapshot());
    }

    [Fact]
    public void AuditPreservesSnapshotsActorInstantAndCorrelationWithoutReasonOrSecrets()
    {
        var device = Create();
        var actor = Guid.NewGuid();
        var created = new DeviceAdministrationAuditEntry(Guid.NewGuid(), actor, DeviceAdministrationOperation.Created,
            null, device.Snapshot(), Now, "synthetic-correlation");
        var before = device.Snapshot();
        device.Rename("Other", Now.AddMinutes(1));
        var renamed = new DeviceAdministrationAuditEntry(Guid.NewGuid(), actor, DeviceAdministrationOperation.Renamed,
            before, device.Snapshot(), Now.AddMinutes(1), "synthetic-correlation");
        Assert.Equal("Camera 1", created.Current.Name);
        Assert.Equal(before, renamed.Previous);
        Assert.Equal("Other", renamed.Current.Name);
        Assert.Equal(actor, renamed.ActorId);
        Assert.Equal(device.Id, renamed.DeviceId);
        Assert.Equal(Now.AddMinutes(1), renamed.OccurredAt);
        Assert.Equal("synthetic-correlation", renamed.CorrelationId);
        Assert.DoesNotContain(typeof(DeviceAdministrationSnapshot).GetProperties(), p =>
            p.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Token", StringComparison.OrdinalIgnoreCase));
        Assert.Throws<ArgumentException>(() => new DeviceAdministrationAuditEntry(Guid.NewGuid(), actor,
            DeviceAdministrationOperation.Renamed, before, before, Now, "correlation"));
        Assert.Throws<ArgumentException>(() => new DeviceAdministrationAuditEntry(Guid.NewGuid(), Guid.Empty,
            DeviceAdministrationOperation.Created, null, device.Snapshot(), device.UpdatedAt, "correlation"));
    }

    [Theory]
    [InlineData(DeviceAdministrationOperation.Deactivated)]
    [InlineData(DeviceAdministrationOperation.Reactivated)]
    [InlineData(DeviceAdministrationOperation.CredentialsRotated)]
    [InlineData(DeviceAdministrationOperation.CredentialsRevoked)]
    public void AuditRecordsEachEffectiveCredentialTransition(DeviceAdministrationOperation operation)
    {
        var device = Create();
        if (operation == DeviceAdministrationOperation.Reactivated) device.Deactivate(Now);
        var previous = device.Snapshot();
        var changedAt = Now.AddMinutes(1);
        switch (operation)
        {
            case DeviceAdministrationOperation.Deactivated: device.Deactivate(changedAt); break;
            case DeviceAdministrationOperation.Reactivated: device.Reactivate(true, changedAt); break;
            case DeviceAdministrationOperation.CredentialsRotated: device.RotateCredentials(true, changedAt); break;
            case DeviceAdministrationOperation.CredentialsRevoked: device.RevokeCredentials(changedAt); break;
        }
        var audit = new DeviceAdministrationAuditEntry(Guid.NewGuid(), Guid.NewGuid(), operation,
            previous, device.Snapshot(), changedAt, "correlation");
        Assert.Equal(operation, audit.Operation);
        Assert.Equal(previous, audit.Previous);
        Assert.Throws<ArgumentException>(() => new DeviceAdministrationAuditEntry(Guid.NewGuid(), Guid.NewGuid(),
            DeviceAdministrationOperation.Renamed, previous, device.Snapshot(), changedAt, "correlation"));
    }

    private static void AssertVersionsChanged(DeviceAdministrationSnapshot before, LprDevice after)
    {
        Assert.NotEqual(before.Version, after.Version);
        Assert.NotEqual(before.AuthorizationVersion, after.AuthorizationVersion);
        Assert.NotEqual(Guid.Empty, after.Version);
        Assert.NotEqual(Guid.Empty, after.AuthorizationVersion);
    }
}
