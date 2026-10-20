using GarajeMetria.Application.Authentication;
using GarajeMetria.Domain.Authentication;
using GarajeMetria.Infrastructure.Persistence;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GarajeMetria.IntegrationTests;

public sealed class SqlStoreTests(SqlFixture database) : IClassFixture<SqlFixture>
{
    [SqlFact]
    public async Task IndependentScopesShareOneAtomicQuotaIncludingFirstPartitionCreation()
    {
        using var factory = new AuthenticationFactory(database);
        _ = factory.Services;
        var attempts = Enumerable.Range(0, 12).Select(async _ =>
        {
            using var scope = factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IAuthenticationRateLimitStore>()
                .AdmitAsync(new("login-user", 5, 60), "same-user", default);
        });
        var results = await Task.WhenAll(attempts);
        Assert.Equal(5, results.Count(x => x.Allowed));
        Assert.All(results.Where(x => !x.Allowed), x => Assert.InRange(x.RetryAfterSeconds, 1, 60));
    }

    [SqlFact]
    public async Task WindowExcludesEventsExactlyAtItsLowerBoundaryAndRejectedRequestsDoNotExtendIt()
    {
        using var factory = new AuthenticationFactory(database);
        using var scope = factory.Services.CreateScope();
        var limiter = scope.ServiceProvider.GetRequiredService<IAuthenticationRateLimitStore>();
        for (var i = 0; i < 5; i++) Assert.True((await limiter.AdmitAsync(new("login-user", 5, 60), "boundary-user", default)).Allowed);
        Assert.False((await limiter.AdmitAsync(new("login-user", 5, 60), "boundary-user", default)).Allowed);
        await using var db = database.CreateContext();
        // Move admitted events to older than the exact lower bound; use SQL's clock.
        await db.Database.ExecuteSqlRawAsync("UPDATE AuthenticationRatePermits SET AcceptedAt=DATEADD(second,-60,TODATETIMEOFFSET(SYSUTCDATETIME(),'+00:00'))");
        Assert.True((await limiter.AdmitAsync(new("login-user", 5, 60), "boundary-user", default)).Allowed);
    }

    [SqlFact]
    public async Task ConcurrentRefreshConsumesOnceAndRevokesTheWinningFamily()
    {
        using var factory = new AuthenticationFactory(database);
        using var loginScope = factory.Services.CreateScope();
        var login = await loginScope.ServiceProvider.GetRequiredService<ISender>().Send(new LoginCommand("user", "synthetic"));
        Assert.True(login.Succeeded);
        var tasks = Enumerable.Range(0, 2).Select(async _ =>
        {
            using var scope = factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ISender>()
                .Send(new RefreshSessionCommand(login.Value!.Tokens.Refresh.Value));
        });
        var results = await Task.WhenAll(tasks);
        Assert.Single(results, x => x.Succeeded);
        Assert.Single(results, x => x.Error?.Code == FailureCode.SessionEnded);
        await using var db = database.CreateContext();
        var user = await db.Users.SingleAsync(u => u.DirectoryObjectId == factory.DirectoryAdapter.Id);
        var session = await db.Sessions.SingleAsync(s => s.UserId == user.Id);
        Assert.NotNull(session.ClosedAt);
        Assert.Equal("refresh_reuse", session.CloseReason);
        Assert.Equal(2, await db.RefreshTokens.CountAsync(t => t.SessionId == session.Id));
    }

    [SqlFact]
    public async Task KnownRollbackDoesNotPartiallyCloseTheExistingSession()
    {
        using var factory = new AuthenticationFactory(database);
        using var loginScope = factory.Services.CreateScope();
        var login = (await loginScope.ServiceProvider.GetRequiredService<ISender>().Send(new LoginCommand("user", "synthetic"))).Value!;
        using var scope = factory.Services.CreateScope();
        var sessionId = scope.ServiceProvider.GetRequiredService<ITokenService>().Validate(login.Tokens.Access.Value, "access")!.SessionId;
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var failure = await Assert.ThrowsAsync<PersistenceFailureException>(() => unit.ExecuteAsync<bool>(Guid.Parse(login.User!.Id), async ct =>
        {
            var repository = scope.ServiceProvider.GetRequiredService<IAuthenticationSessionRepository>();
            var session = (await repository.FindAsync(sessionId, ct))!;
            session.Close(factory.Clock.Now, "synthetic_failure");
            await scope.ServiceProvider.GetRequiredService<AuthenticationDbContext>().SaveChangesAsync(ct);
            throw new InvalidOperationException("synthetic programming failure");
        }, default));
        Assert.Equal(FailureCode.InternalError, failure.Code);
        await using var db = database.CreateContext();
        Assert.Null((await db.Sessions.SingleAsync(s => s.Id == sessionId)).ClosedAt);
    }

    [SqlFact]
    public async Task BearerRequiresAnAccessTokenAndThePersistedActiveSession()
    {
        using var factory = new AuthenticationFactory(database);
        using var scope = factory.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var result = (await sender.Send(new LoginCommand("user", "synthetic"))).Value!;
        async Task<bool> Authenticate(string token)
        {
            using var requestScope = factory.Services.CreateScope();
            var context = new DefaultHttpContext { RequestServices = requestScope.ServiceProvider };
            context.Request.Headers.Authorization = "Bearer " + token;
            return (await context.AuthenticateAsync("Bearer")).Succeeded;
        }
        Assert.True(await Authenticate(result.Tokens.Access.Value));
        Assert.False(await Authenticate(result.Tokens.Refresh.Value));
        Assert.True((await sender.Send(new LogoutCommand(result.Tokens.Refresh.Value))).Succeeded);
        Assert.False(await Authenticate(result.Tokens.Access.Value));
    }

    [SqlFact]
    public async Task CleanupRetainsActiveFamiliesAndDeletesOnlyEligibleRecordsWithoutCascades()
    {
        using var factory = new AuthenticationFactory(database);
        using var scope = factory.Services.CreateScope();
        var login = (await scope.ServiceProvider.GetRequiredService<ISender>().Send(new LoginCommand("user", "synthetic"))).Value!;
        var userId = Guid.Parse(login.User!.Id);
        var oldId = Guid.NewGuid();
        var oldRefreshId = Guid.NewGuid();
        await using (var db = database.CreateContext())
        {
            var now = DateTimeOffset.UtcNow;
            var old = new AuthenticationSession(oldId, userId, "administrator", now.AddDays(-40), now.AddDays(-39));
            old.Close(now.AddDays(-39), "expired");
            db.Sessions.Add(old);
            db.RefreshTokens.Add(new(oldRefreshId, oldId, new string('F', 64), TokenTimes.FloorSecond(now.AddDays(-40)), TokenTimes.FloorSecond(now.AddDays(-39))));
            var audit = await db.ProvisioningAudits.SingleAsync(a => a.UserId == userId);
            audit.OccurredAt = now.AddDays(-366); // Synthetic history only; production exposes no audit edit API.
            await db.CleanupSchedules.ExecuteDeleteAsync();
            await db.SaveChangesAsync();
        }
        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CleanupAuthenticationCommand("test-owner"));
        Assert.True(result.Acquired);
        await using var check = database.CreateContext();
        Assert.False(await check.Sessions.AnyAsync(s => s.Id == oldId));
        Assert.False(await check.RefreshTokens.AnyAsync(t => t.Id == oldRefreshId));
        Assert.True(await check.Users.AnyAsync(u => u.Id == userId));
        Assert.True(await check.Sessions.AnyAsync(s => s.UserId == userId && s.ClosedAt == null));
        Assert.False(await check.ProvisioningAudits.AnyAsync(a => a.UserId == userId));
        Assert.False((await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CleanupAuthenticationCommand("other-owner"))).Acquired);
    }
}
