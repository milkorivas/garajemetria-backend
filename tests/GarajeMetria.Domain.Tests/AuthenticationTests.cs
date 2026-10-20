using GarajeMetria.Domain.Authentication;

namespace GarajeMetria.Domain.Tests;

public sealed class AuthenticationTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false, false, null)]
    [InlineData(false, true, "advisor")]
    [InlineData(true, false, "administrator")]
    [InlineData(true, true, "administrator")]
    public void RoleUsesMembershipWithAdministratorPriority(bool admin, bool advisor, string? expected) =>
        Assert.Equal(expected, UserRoles.Resolve(admin, advisor));

    [Theory]
    [InlineData("administrator", false, false, false, WorkshopAccessDecision.Allowed)]
    [InlineData("administrator", true, false, false, WorkshopAccessDecision.Allowed)]
    [InlineData("advisor", false, false, false, WorkshopAccessDecision.WorkshopRequired)]
    [InlineData("advisor", true, false, false, WorkshopAccessDecision.CatalogUnavailable)]
    [InlineData("advisor", true, true, false, WorkshopAccessDecision.WorkshopInactive)]
    [InlineData("advisor", true, true, true, WorkshopAccessDecision.Allowed)]
    public void WorkshopPolicyIsIndependentOfInfrastructure(string role, bool assigned, bool catalog, bool active, WorkshopAccessDecision expected) =>
        Assert.Equal(expected, UserAccessPolicy.Evaluate(role, assigned, catalog, active));

    [Fact]
    public void SessionAuthorizationBindsTheUserAndExactRole()
    {
        var session = Session();
        Assert.True(session.Allows(session.UserId, "administrator", Start));
        Assert.False(session.Allows(session.UserId, "Administrator", Start));
        Assert.False(session.Allows(Guid.NewGuid(), "administrator", Start));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void SessionHasExclusiveAbsoluteBoundary(int ticks, bool active)
    {
        var session = Session();
        Assert.Equal(active, session.IsActive(session.AbsoluteExpiresAt.AddTicks(ticks)));
    }

    [Fact]
    public void ClosingIsIrreversibleAndCannotMoveRetentionStart()
    {
        var session = Session();
        session.Close(Start.AddMinutes(2), "logout");
        session.Close(Start.AddDays(2), "late_close");
        Assert.Equal(Start.AddMinutes(2), session.EndedAt);
        Assert.Equal("logout", session.CloseReason);
        Assert.False(session.IsActive(Start.AddMinutes(3)));
    }

    [Fact]
    public void LateCloseDoesNotDelayEndBeyondAbsoluteExpiry()
    {
        var session = Session();
        session.Close(Start.AddDays(1), "late_close");
        Assert.Equal(session.AbsoluteExpiresAt, session.EndedAt);
    }

    [Theory]
    [InlineData(-1, RefreshUse.Reused)]
    [InlineData(0, RefreshUse.Expired)]
    [InlineData(1, RefreshUse.Expired)]
    public void ConsumedTokenOnlyMeansReplayBeforeExpiry(int ticks, RefreshUse expected)
    {
        var session = Session();
        var token = new RefreshTokenRecord(Guid.NewGuid(), session.Id, new('A', 64), Start, Start.AddMinutes(30));
        Assert.Equal(RefreshUse.Accepted, token.Consume(session, Start.AddMinutes(1)));
        Assert.Equal(expected, token.Inspect(session, token.ExpiresAt.AddTicks(ticks)));
    }

    [Fact]
    public void RotationDoesNotExtendSessionOrConsumeTwice()
    {
        var session = Session();
        var token = new RefreshTokenRecord(Guid.NewGuid(), session.Id, new('A', 64), Start, Start.AddMinutes(30));
        Assert.Equal(RefreshUse.Accepted, token.Consume(session, Start.AddMinutes(1)));
        Assert.Equal(RefreshUse.Reused, token.Consume(session, Start.AddMinutes(2)));
        Assert.Equal(Start.AddMinutes(1), token.ConsumedAt);
        Assert.Equal(Start.AddHours(8), session.AbsoluteExpiresAt);
    }

    [Fact]
    public void TokenExpiryIsFlooredAndCappedAtTheRealSessionEnd()
    {
        var end = Start.AddSeconds(10).AddMilliseconds(987);
        Assert.Equal(Start.AddSeconds(10), TokenTimes.Expires(Start, TimeSpan.FromMinutes(30), end));
    }

    private static AuthenticationSession Session() => new(Guid.NewGuid(), Guid.NewGuid(), UserRoles.Administrator, Start, Start.AddHours(8));
}
