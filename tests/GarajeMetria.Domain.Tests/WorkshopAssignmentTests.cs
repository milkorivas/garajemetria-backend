using GarajeMetria.Domain.Authentication;
using GarajeMetria.Domain.Users;

namespace GarajeMetria.Domain.Tests;

public sealed class WorkshopAssignmentTests
{
    [Theory]
    [InlineData(UserRoles.Administrator, true)]
    [InlineData(UserRoles.Advisor, false)]
    [InlineData(null, false)]
    public void RF002_CP019_OnlyAdministratorMayHaveAssignmentRemoved(string? role, bool allowed) =>
        Assert.Equal(allowed, AssignmentRules.CanRemove(role));

    [Fact]
    public void RF002_CP016_UnicodeLimitsCountScalarsAndPreserveReason()
    {
        var reason = string.Concat(Enumerable.Repeat("🚗", 500));
        Assert.True(AssignmentRules.ValidText(reason, 500));
        Assert.False(AssignmentRules.ValidText(reason + "x", 500));
        Assert.False(AssignmentRules.ValidText("\ud800", 500));
        Assert.False(AssignmentRules.ValidText("reason\n", 500));
        Assert.False(AssignmentRules.ValidText("   ", 500));
        Assert.False(AssignmentRules.ValidText(" workshop", 128, true));
        var audit = new WorkshopAssignmentAuditEntry(Guid.NewGuid(), Guid.NewGuid(), null, "workshop", reason,
            DateTimeOffset.UnixEpoch, "correlation");
        Assert.Equal(reason, audit.Reason);
        Assert.NotEqual(Guid.Empty, audit.Id);
    }

    [Fact]
    public void RF002_CP017_NoChangeCannotProduceChangeAudit() =>
        Assert.Throws<ArgumentException>(() => new WorkshopAssignmentAuditEntry(Guid.NewGuid(), Guid.NewGuid(), "same", "same",
            "Reason", DateTimeOffset.UnixEpoch, "correlation"));
}
