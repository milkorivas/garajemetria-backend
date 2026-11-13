using System.Data;
using System.Data.Common;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Domain.Lpr;
using GarajeMetria.Infrastructure.Lpr;
using GarajeMetria.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;

namespace GarajeMetria.IntegrationTests;

public sealed class LprRetentionTests(SqlFixture database) : IClassFixture<SqlFixture>
{
    private static readonly RetentionPolicy Policy = new(30, 24, 365, 24, 100);

    [SqlFact]
    public async Task CleanupDeletesOnlyEligibleDeviceAuditsAndExpiredTokenMetadataWithoutCascades()
    {
        var seed = await SeedAsync();
        await using var db = database.CreateContext();
        var now = seed.Now.UtcDateTime;
        var oldValid = Token(seed.Device, now.AddDays(-3), now.AddHours(-25), "valid");
        var oldRevoked = Token(seed.Device, now.AddDays(-3), now.AddHours(-25), "revoked");
        var recentRedemption = Token(seed.Device, now.AddDays(-3), now.AddHours(-25), "revoked");
        recentRedemption.RedemptionDate = now.AddHours(-1);
        var retained = new[]
        {
            Token(seed.Device, now.AddDays(-3), now.AddHours(-23), "valid"),
            Token(seed.Device, now.AddDays(-3), now.AddHours(1), "valid"),
            Token(seed.Device, now.AddDays(-3), now.AddHours(1), "revoked"),
            Token(seed.Device, now.AddHours(-1), now.AddHours(-25), "revoked"),
            Token(seed.Device, now.AddDays(-3), null, "revoked"),
            Token(seed.Device, now.AddDays(-3), now.AddHours(-25), "unknown"),
            Token(seed.Device, now.AddDays(-3), now.AddHours(-25), "valid", "refresh_token"),
            Token(seed.Device, now.AddDays(-3), now.AddHours(-25), "VALID"),
            Token(seed.Device, now.AddDays(-3), now.AddHours(-25), "valid", "ACCESS_TOKEN"),
            Token(seed.Device, now.AddDays(-3), now.AddHours(-25), "valid "),
            Token(seed.Device, now.AddDays(-3), now.AddHours(-25), "valid", "access_token "),
            recentRedemption
        };
        var foreignApp = new OpenIddictEntityFrameworkCoreApplication { Id = Guid.NewGuid().ToString("N") };
        db.Add(foreignApp);
        var foreign = Token(Guid.Parse(foreignApp.Id), now.AddDays(-3), now.AddHours(-25), "revoked");
        var foreignEntry = db.Add(foreign);
        foreignEntry.Reference(x => x.Application).CurrentValue = foreignApp;
        AddTokens(db, retained);
        AddTokens(db, oldValid, oldRevoked);
        await db.SaveChangesAsync();
        var result = await Store(db).CleanBatchAsync("retention", Policy, default);
        Assert.True(result.Acquired);
        Assert.Equal(2, result.DeviceTokens);
        Assert.Equal(1, result.AuditEntries);
        Assert.False(result.More);
        Assert.False(await db.Set<OpenIddictEntityFrameworkCoreToken>().AnyAsync(x => x.Id == oldValid.Id || x.Id == oldRevoked.Id));
        foreach (var token in retained.Append(foreign)) Assert.True(await db.Set<OpenIddictEntityFrameworkCoreToken>().AnyAsync(x => x.Id == token.Id));
        Assert.False(await db.DeviceAdministrationAudits.AnyAsync(x => x.Id == seed.OldAudit));
        Assert.True(await db.DeviceAdministrationAudits.AnyAsync(x => x.Id == seed.RecentAudit));
        Assert.True(await db.LprDevices.AnyAsync(x => x.Id == seed.Device));
        Assert.True(await db.LprEvents.AnyAsync(x => x.EventId == seed.Event));
        Assert.Equal("synthetic-hash", (await db.Set<OpenIddictEntityFrameworkCoreApplication>().AsNoTracking().SingleAsync(x => x.Id == seed.Device.ToString("N"))).ClientSecret);
    }

    [SqlFact]
    public async Task ConfiguredMarginAndInclusiveBoundaryUseTheSharedSqlClock()
    {
        var seed = await SeedAsync();
        await using var db = new AuthenticationDbContext(new DbContextOptionsBuilder<AuthenticationDbContext>().UseSqlServer(database.ConnectionString).AddInterceptors(new FrozenClock(seed.Now)).Options);
        var old = Token(seed.Device, seed.Now.UtcDateTime.AddDays(-4), seed.Now.UtcDateTime.AddHours(-24), "valid");
        var recent = Token(seed.Device, seed.Now.UtcDateTime.AddDays(-4), seed.Now.UtcDateTime.AddHours(-24).AddTicks(1), "valid");
        AddTokens(db, old, recent);
        await db.SaveChangesAsync();
        Assert.Equal(0, (await Store(db, hours: 48).CleanBatchAsync("margin", Policy, default)).DeviceTokens);
        await ResetScheduleAsync(db);
        Assert.Equal(1, (await Store(db, hours: 24).CleanBatchAsync("margin", Policy, default)).DeviceTokens);
        Assert.True(await db.Set<OpenIddictEntityFrameworkCoreToken>().AnyAsync(x => x.Id == recent.Id));
    }

    [SqlFact]
    public async Task ConcurrentOwnersAreFencedAndBatchesRetainEligibleRemainders()
    {
        var seed = await SeedAsync();
        await using var first = database.CreateContext();
        await using var second = database.CreateContext();
        AddTokens(first, Enumerable.Range(0, 3).Select(_ => Token(seed.Device, seed.Now.UtcDateTime.AddDays(-4), seed.Now.UtcDateTime.AddDays(-3), "valid")).ToArray());
        await first.SaveChangesAsync();
        var policy = Policy with { BatchSize = 2 };
        var results = await Task.WhenAll(Store(first).CleanBatchAsync("owner-a", policy, default), Store(second).CleanBatchAsync("owner-b", policy, default));
        var acquired = Assert.Single(results, x => x.Acquired);
        Assert.True(acquired.More);
        Assert.Equal(2, acquired.DeviceTokens);
        var owner = (await first.CleanupSchedules.AsNoTracking().SingleAsync()).Owner!;
        Assert.Equal(1, (await Store(first).CleanBatchAsync(owner, policy, default)).DeviceTokens);
        Assert.False((await Store(second).CleanBatchAsync("other", policy, default)).Acquired);
    }

    [SqlTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrExpiredLeaseRollsBackTheWholeBatch(bool expireLease)
    {
        var seed = await SeedAsync();
        using var cancellation = new CancellationTokenSource();
        var fault = new CleanupFault(cancellation, expireLease);
        await using var db = new AuthenticationDbContext(new DbContextOptionsBuilder<AuthenticationDbContext>().UseSqlServer(database.ConnectionString).AddInterceptors(fault).Options);
        var token = Token(seed.Device, seed.Now.UtcDateTime.AddDays(-4), seed.Now.UtcDateTime.AddDays(-3), "valid");
        AddTokens(db, token);
        await db.SaveChangesAsync();
        fault.Enabled = true;
        if (expireLease)
            Assert.Equal(FailureCode.ServiceUnavailable, (await Assert.ThrowsAsync<PersistenceFailureException>(() => Store(db).CleanBatchAsync("fault", Policy, cancellation.Token))).Code);
        else
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store(db).CleanBatchAsync("fault", Policy, cancellation.Token));
        await using var check = database.CreateContext();
        Assert.True(await check.DeviceAdministrationAudits.AnyAsync(x => x.Id == seed.OldAudit));
        Assert.True(await check.Set<OpenIddictEntityFrameworkCoreToken>().AnyAsync(x => x.Id == token.Id));
        Assert.False(await check.CleanupSchedules.AnyAsync());
    }

    private static RetentionStore Store(AuthenticationDbContext db, int hours = 24) =>
        new(db, new AuthenticationOptions(), NullLogger<RetentionStore>.Instance, deviceOAuth: new DeviceOAuthOptions { DeviceTokenRetentionHours = hours });
    private static async Task ResetScheduleAsync(AuthenticationDbContext db) { db.ChangeTracker.Clear(); await db.CleanupSchedules.ExecuteDeleteAsync(); }
    private async Task<(Guid Device, Guid Event, Guid OldAudit, Guid RecentAudit, DateTimeOffset Now)> SeedAsync()
    {
        await using var db = database.CreateContext();
        await ResetScheduleAsync(db);
        // Each case owns this isolated SQL fixture; reset prior cases' test metadata only.
        await db.Set<OpenIddictEntityFrameworkCoreToken>().ExecuteDeleteAsync();
        await db.DeviceAdministrationAudits.ExecuteDeleteAsync();
        var now = await SqlTransactions.NowAsync(db, default);
        var id = Guid.NewGuid();
        var site = id.ToString("N");
        var actor = Guid.NewGuid();
        db.Users.Add(new() { Id = actor, DirectoryObjectId = Guid.NewGuid() });
        db.Workshops.Add(new() { Id = site, Code = site, Name = "Synthetic", IsActive = true });
        db.Add(new OpenIddictEntityFrameworkCoreApplication { Id = id.ToString("N"), ClientSecret = "synthetic-hash" });
        var device = new LprDevice(id, "Synthetic", site, true, now.AddDays(-366));
        db.LprDevices.Add(device).Property<string>("OAuthApplicationId").CurrentValue = id.ToString("N");
        var oldAudit = Guid.NewGuid();
        db.DeviceAdministrationAudits.Add(new()
        {
            Id = oldAudit,
            ActorId = actor,
            DeviceId = id,
            Operation = DeviceAdministrationOperation.Created,
            OccurredAt = device.UpdatedAt,
            CorrelationId = "synthetic",
            Current = device.Snapshot()
        });
        var previous = device.Snapshot();
        device.Rename("Recent name", now.AddDays(-364));
        var recentAudit = Guid.NewGuid();
        db.DeviceAdministrationAudits.Add(new()
        {
            Id = recentAudit,
            ActorId = actor,
            DeviceId = id,
            Operation = DeviceAdministrationOperation.Renamed,
            OccurredAt = device.UpdatedAt,
            CorrelationId = "synthetic",
            Previous = previous,
            Current = device.Snapshot()
        });
        var observation = new LprEvent(Guid.NewGuid(), id, site, now.AddYears(-2), now.AddYears(-2), "synthetic-invalid", 0m, null);
        db.LprEvents.Add(new()
        {
            EventId = observation.EventId,
            DeviceId = id,
            WorkshopId = site,
            CapturedAt = observation.CapturedAt,
            ReceivedAt = observation.ReceivedAt,
            PlateOriginal = observation.PlateOriginal,
            PlateNormalized = observation.PlateNormalized,
            PlateStatus = observation.PlateStatus,
            PlateRulesVersion = observation.PlateRulesVersion,
            Confidence = 0m
        });
        await db.SaveChangesAsync();
        return (id, observation.EventId, oldAudit, recentAudit, now);
    }
    private static OpenIddictEntityFrameworkCoreToken Token(Guid device, DateTime creation, DateTime? expiration, string status, string type = "access_token") => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        CreationDate = creation,
        ExpirationDate = expiration,
        Status = status,
        Type = type,
        Subject = device.ToString("N")
    };
    private static void AddTokens(AuthenticationDbContext db, params OpenIddictEntityFrameworkCoreToken[] tokens)
    {
        foreach (var token in tokens) db.Add(token).Property<string>("ApplicationId").CurrentValue = token.Subject!;
    }
    private sealed class FrozenClock(DateTimeOffset now) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (!command.CommandText.Contains("SYSUTCDATETIME", StringComparison.Ordinal)) return ValueTask.FromResult(result);
            var table = new DataTable();
            table.Columns.Add("Value", typeof(DateTimeOffset));
            table.Rows.Add(now);
            return ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(table.CreateDataReader()));
        }
    }
    private sealed class CleanupFault(CancellationTokenSource cancellation, bool expireLease) : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData data, int result, CancellationToken ct = default)
        {
            if (Enabled && command.CommandText.Contains("DELETE", StringComparison.Ordinal) && command.CommandText.Contains("DeviceAdministrationAudits", StringComparison.Ordinal))
            {
                if (expireLease) data.Context!.ChangeTracker.Entries<CleanupSchedule>().Single().Entity.LeaseUntil = DateTimeOffset.UnixEpoch;
                else cancellation.Cancel();
            }
            return ValueTask.FromResult(result);
        }
    }
}
