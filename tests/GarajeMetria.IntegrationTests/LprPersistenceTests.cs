using System.Data.Common;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Lpr;
using GarajeMetria.Domain.Lpr;
using GarajeMetria.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Core;
using OpenIddict.EntityFrameworkCore.Models;

namespace GarajeMetria.IntegrationTests;

public sealed class SqlTheoryAttribute : TheoryAttribute
{
    public SqlTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GARAJEMETRIA_TEST_SQL")))
            Skip = "Set GARAJEMETRIA_TEST_SQL to an isolated SQL Server test instance.";
    }
}

public sealed class LprMigrationTests(SqlFixture database) : IClassFixture<SqlFixture>
{
    [SqlFact]
    public async Task EmptySchemaAndUpgradePreserveExistingAuthenticationAdministrationAndCatalog()
    {
        await using var db = database.CreateContext();
        Assert.Empty(await db.LprDevices.ToArrayAsync());
        Assert.Empty(await db.LprEvents.ToArrayAsync());
        Assert.Empty(await db.Set<OpenIddictEntityFrameworkCoreApplication>().ToArrayAsync());
        var migrator = db.GetService<IMigrator>();
        Assert.Empty(await db.VehicleStayPartitions.ToArrayAsync());
        await db.StayConfiguration.ExecuteDeleteAsync();
        await db.StayPolicies.ExecuteDeleteAsync();
        await migrator.MigrateAsync("20261029161807_WorkshopAssignments");
        var actor = Guid.NewGuid();
        var target = Guid.NewGuid();
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        db.Workshops.Add(new() { Id = "migration-lpr", Code = "migration-lpr", Name = "Synthetic", IsActive = true });
        db.Users.AddRange(new LocalUser { Id = actor, DirectoryObjectId = Guid.NewGuid(), Username = "actor", DisplayName = "Actor", DefaultWorkshopId = "migration-lpr" },
            new LocalUser { Id = target, DirectoryObjectId = Guid.NewGuid(), Username = "target", DisplayName = "Target", DefaultWorkshopId = "migration-lpr" });
        db.Catalog.Add(new() { Id = 1, LastPublishedAt = now });
        db.ProvisioningAudits.Add(new() { Id = Guid.NewGuid(), UserId = actor, OccurredAt = now });
        db.AssignmentAudits.Add(new(actor, target, null, "migration-lpr", "Synthetic assignment", now, "synthetic"));
        var session = new GarajeMetria.Domain.Authentication.AuthenticationSession(Guid.NewGuid(), actor, "administrator", now, now.AddHours(1));
        db.Sessions.Add(session);
        db.RefreshTokens.Add(new(Guid.NewGuid(), session.Id, new string('a', 64), now, now.AddMinutes(30)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await migrator.MigrateAsync();
        Assert.Equal(2, await db.Users.CountAsync());
        Assert.All(await db.Users.AsNoTracking().ToArrayAsync(), x => Assert.Equal("migration-lpr", x.DefaultWorkshopId));
        Assert.Equal(1, await db.Sessions.CountAsync());
        Assert.Equal(1, await db.RefreshTokens.CountAsync());
        Assert.Equal(1, await db.ProvisioningAudits.CountAsync());
        Assert.Equal(1, await db.AssignmentAudits.CountAsync());
        Assert.Equal(now, (await db.Catalog.SingleAsync()).LastPublishedAt);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Empty(await db.LprDevices.ToArrayAsync());
        Assert.Empty(await db.Set<OpenIddictEntityFrameworkCoreApplication>().ToArrayAsync());
        var cascades = await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.foreign_keys WHERE (OBJECT_NAME(parent_object_id) LIKE 'Lpr%' OR OBJECT_NAME(parent_object_id) LIKE 'OpenIddict%' OR OBJECT_NAME(parent_object_id) = 'DeviceAdministrationAudits') AND delete_referential_action <> 0").SingleAsync();
        Assert.Equal(0, cascades);
    }
}

public sealed class LprPersistenceTests(SqlFixture database) : IClassFixture<SqlFixture>
{
    private static readonly DateTimeOffset Instant = DateTimeOffset.UnixEpoch.AddTicks(1234567);

    [SqlFact]
    public async Task CoreManagerDeviceEventAndAuditCommitInTheSameScopedTransactionWithExactValues()
    {
        using var factory = new AuthenticationFactory(database);
        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider;
        Assert.True(provider.GetRequiredService<IOptions<OpenIddictCoreOptions>>().Value.DisableEntityCaching);
        var seed = await SeedAsync();
        var device = new LprDevice(Guid.NewGuid(), "Synthetic camera", seed.Site, true, Instant);
        var eventId = Guid.NewGuid();
        var uow = provider.GetRequiredService<ILprUnitOfWork>();
        await uow.ExecuteAsync(new(seed.Actor, device.Id, eventId), async ct =>
        {
            var manager = provider.GetRequiredService<IOpenIddictApplicationManager>();
            await manager.CreateAsync(Application(device.Id), "synthetic-client-secret", ct);
            provider.GetRequiredService<ILprDeviceRepository>().Add(device);
            provider.GetRequiredService<IDeviceAdministrationAuditRepository>().Add(new(Guid.NewGuid(), seed.Actor,
                DeviceAdministrationOperation.Created, null, device.Snapshot(), Instant, "synthetic"));
            Assert.Equal(LprEventComparison.Missing, (await provider.GetRequiredService<ILprEventRepository>().CompareAsync(
                new(eventId, device.Id, seed.Site, Instant, "0000-bbb", 0.12345678m, LprDirection.Entry), ct)).Comparison);
            provider.GetRequiredService<ILprEventRepository>().Add(new LprEvent(eventId, device.Id, seed.Site,
                Instant, Instant.AddTicks(1), "0000-bbb", 0.12345678m, LprDirection.Entry));
            var clock = await provider.GetRequiredService<ILprClock>().GetUtcNowAsync(ct);
            Assert.Equal(TimeSpan.Zero, clock.Offset);
            Assert.InRange(clock, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
            return true;
        }, default);
        await using var verify = database.CreateContext();
        var row = await verify.LprEvents.SingleAsync(x => x.EventId == eventId);
        Assert.Equal(Instant, row.CapturedAt);
        Assert.Equal(Instant.AddTicks(1), row.ReceivedAt);
        Assert.Equal(0.12345678m, row.Confidence);
        Assert.Equal("0000-bbb", row.PlateOriginal);
        Assert.Equal("0000BBB", row.PlateNormalized);
        Assert.Equal(LprDirection.Entry, row.Direction);
        var storedDevice = await provider.GetRequiredService<ILprDeviceRepository>().FindAsync(device.Id, default);
        Assert.Equal(device.Snapshot(), storedDevice!.Snapshot());
        var audit = await verify.DeviceAdministrationAudits.SingleAsync(x => x.DeviceId == device.Id);
        Assert.Null(audit.Previous);
        Assert.Equal(device.Snapshot(), audit.Current);
        Assert.NotEqual("synthetic-client-secret", (await verify.Set<OpenIddictEntityFrameworkCoreApplication>().SingleAsync(x => x.Id == device.Id.ToString("N"))).ClientSecret);
    }

    [SqlTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagerInternalSaveAndDeviceWritesRollBackOnExceptionOrFailedOutcome(bool returnFailure)
    {
        using var factory = new AuthenticationFactory(database);
        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider;
        var seed = await SeedAsync();
        var id = Guid.NewGuid();
        var uow = provider.GetRequiredService<ILprUnitOfWork>();
        async Task<Outcome<bool>> Act(CancellationToken ct)
        {
            provider.GetRequiredService<ILprDeviceRepository>().Add(new(id, "Synthetic", seed.Site, true, Instant));
            await provider.GetRequiredService<IOpenIddictApplicationManager>().CreateAsync(Application(id), "synthetic-secret", ct);
            Assert.True(await provider.GetRequiredService<AuthenticationDbContext>().LprDevices.AsNoTracking().AnyAsync(x => x.Id == id, ct));
            if (returnFailure) return Outcome<bool>.Fail(FailureCode.PreconditionFailed);
            throw new InvalidOperationException("Synthetic audit failure");
        }
        if (returnFailure) Assert.Equal(FailureCode.PreconditionFailed, (await uow.ExecuteAsync(new(seed.Actor, id), Act, default)).Error?.Code);
        else Assert.Equal(FailureCode.InternalError, (await Assert.ThrowsAsync<PersistenceFailureException>(() => uow.ExecuteAsync(new(seed.Actor, id), Act, default))).Code);
        await using var verify = database.CreateContext();
        Assert.False(await verify.Set<OpenIddictEntityFrameworkCoreApplication>().AnyAsync(x => x.Id == id.ToString("N")));
        Assert.False(await verify.LprDevices.AnyAsync(x => x.Id == id));
        Assert.Empty(provider.GetRequiredService<AuthenticationDbContext>().ChangeTracker.Entries());
    }

    [SqlFact]
    public async Task ConditionalVersionsAndFreshReadsPreserveAssociationAndSnapshots()
    {
        var seed = await SeedAsync(true);
        await using var first = database.CreateContext();
        await using var second = database.CreateContext();
        var repository = new LprRepository(first);
        var device = (await repository.FindAsync(seed.Device!.Id, default))!;
        var before = device.Snapshot();
        device.Rename("Changed", Instant.AddTicks(1));
        var uow = Uow(first);
        Assert.True(await uow.ExecuteAsync(new(seed.Actor, device.Id), async ct =>
        {
            var saved = await repository.SaveAsync(device, before.Version, ct);
            if (saved) repository.Add(new DeviceAdministrationAuditEntry(Guid.NewGuid(), seed.Actor,
                DeviceAdministrationOperation.Renamed, before, device.Snapshot(), device.UpdatedAt, "synthetic"));
            return saved;
        }, default));
        var stale = (await new LprRepository(second).FindAsync(device.Id, default))!;
        stale.Rename("Stale attempt", Instant.AddTicks(2));
        Assert.False(await Uow(second).ExecuteAsync(new(seed.Actor, device.Id), ct => new LprRepository(second).SaveAsync(stale, before.Version, ct), default));
        var current = (await repository.FindAsync(device.Id, default))!;
        Assert.Equal("Changed", current.Name);
        Assert.Equal(before.WorkshopId, current.WorkshopId);
        Assert.Equal(before.AuthorizationVersion, current.AuthorizationVersion);
        var audit = await first.DeviceAdministrationAudits.AsNoTracking().SingleAsync(x => x.DeviceId == device.Id);
        Assert.Equal(before, audit.Previous);
        Assert.Equal(device.Snapshot(), audit.Current);
        var authQuery = (IDeviceAuthorizationQuery)repository;
        Assert.True((await authQuery.FindAsync(device.Id, default))!.WorkshopIsActive);
        await second.Workshops.Where(x => x.Id == seed.Site).ExecuteUpdateAsync(x => x.SetProperty(w => w.IsActive, false));
        Assert.False((await authQuery.FindAsync(device.Id, default))!.WorkshopIsActive);
        var query = (ILprDeviceAdministrationQuery)repository;
        Assert.Equal("Changed", (await query.FindAsync(device.Id, default))!.Name);
        Assert.Single((await query.ListAsync(1, 20, seed.Site, true, default)).Items);
        Assert.Empty((await query.ListAsync(2, 20, seed.Site, null, default)).Items);
    }

    [SqlFact]
    public async Task ComparisonUsesOrdinalOriginalValuesAndNeverReclassifiesOrRevealsForeignEvents()
    {
        var seed = await SeedAsync(true);
        await using var db = database.CreateContext();
        var repository = new LprRepository(db);
        var eventId = Guid.NewGuid();
        var request = new ReceiveLprEventCommand(eventId, seed.Device!.Id, seed.Site, Instant, "0000-bbb", 0.1m);
        Assert.Equal(LprEventComparison.Missing, (await repository.CompareAsync(request, default)).Comparison);
        await Uow(db).ExecuteAsync(new(null, seed.Device.Id, eventId), ct =>
        {
            repository.Add(new LprEvent(eventId, seed.Device.Id, seed.Site, Instant, Instant.AddYears(1), "0000-bbb", 0.1m, null));
            return Task.FromResult(true);
        }, default);
        // Historical metadata must survive a new classifier version, even when it differs from today's result.
        await db.LprEvents.Where(x => x.EventId == eventId).ExecuteUpdateAsync(x => x.SetProperty(r => r.PlateRulesVersion, "es-legacy"));
        var same = await repository.CompareAsync(request with { CapturedAt = Instant.ToOffset(TimeSpan.FromHours(1)), Confidence = 0.100000000m }, default);
        Assert.Equal(LprEventComparison.Identical, same.Comparison);
        Assert.Equal(Instant.AddYears(1), same.Receipt!.ReceivedAt);
        foreach (var changed in new[] { request with { PlateOriginal = "0000BBB" }, request with { PlateOriginal = "0000-bbb " },
                     request with { WorkshopId = seed.Site.ToUpperInvariant() }, request with { DeviceId = Guid.NewGuid() },
                     request with { Confidence = 0.2m }, request with { CapturedAt = Instant.AddTicks(1) }, request with { Direction = LprDirection.Exit } })
        {
            var conflict = await repository.CompareAsync(changed, default);
            Assert.Equal(LprEventComparison.Conflict, conflict.Comparison);
            Assert.Null(conflict.Receipt);
        }
        Assert.Equal("es-legacy", (await db.LprEvents.SingleAsync(x => x.EventId == eventId)).PlateRulesVersion);
    }

    [SqlFact]
    public async Task ConcurrentDeliveriesSerializeOnTheUuidAndGlobalKeyRemainsUnique()
    {
        var seed = await SeedAsync(true);
        var eventId = Guid.NewGuid();
        async Task<int> Deliver()
        {
            await using var db = database.CreateContext();
            var repository = new LprRepository(db);
            return await Uow(db).ExecuteAsync(new(null, seed.Device!.Id, eventId), async ct =>
            {
                var match = await repository.CompareAsync(new(eventId, seed.Device.Id, seed.Site, Instant, "invalid-format", 0m), ct);
                if (match.Comparison == LprEventComparison.Identical) return 0;
                repository.Add(new LprEvent(eventId, seed.Device.Id, seed.Site, Instant, Instant, "invalid-format", 0m, null));
                return 1;
            }, default);
        }
        Assert.Equal(1, (await Task.WhenAll(Deliver(), Deliver())).Sum());
        await using var verify = database.CreateContext();
        Assert.Equal(1, await verify.LprEvents.CountAsync(x => x.EventId == eventId));
        var duplicate = await Assert.ThrowsAsync<PersistenceFailureException>(() => Uow(verify).ExecuteAsync(new(null, seed.Device!.Id), ct =>
        {
            new LprRepository(verify).Add(new LprEvent(eventId, seed.Device.Id, seed.Site, Instant, Instant, "invalid-format", 0m, null));
            return Task.FromResult(true);
        }, default));
        Assert.Contains(SqlNumber(duplicate), new[] { 2601, 2627 });
    }

    [SqlTheory]
    [InlineData("UPDATE LprEvents SET Confidence=1.1 WHERE EventId={0}")]
    [InlineData("UPDATE LprEvents SET Direction='other' WHERE EventId={0}")]
    [InlineData("UPDATE LprEvents SET PlateStatus='invalid',PlateFormat='unexpected' WHERE EventId={0}")]
    [InlineData("UPDATE LprEvents SET ReceivedAt=SWITCHOFFSET(ReceivedAt,'+01:00') WHERE EventId={0}")]
    public async Task PersistentChecksRejectInvalidValues(string sql)
    {
        var seed = await SeedAsync(true);
        await using var db = database.CreateContext();
        var eventId = Guid.NewGuid();
        await Uow(db).ExecuteAsync(new(null, seed.Device!.Id, eventId), ct =>
        {
            new LprRepository(db).Add(new LprEvent(eventId, seed.Device.Id, seed.Site, Instant, Instant, "0000BBB", 1m, null));
            return Task.FromResult(true);
        }, default);
        var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(sql, eventId));
        Assert.Equal(547, error.Number);
    }

    [SqlFact]
    public async Task CompositeWorkshopForeignKeyAndRestrictiveDeletesProtectHistory()
    {
        var seed = await SeedAsync(true);
        var other = await SeedAsync();
        await using var db = database.CreateContext();
        var error = await Assert.ThrowsAsync<PersistenceFailureException>(() => Uow(db).ExecuteAsync(new(null, seed.Device!.Id), ct =>
        {
            new LprRepository(db).Add(new LprEvent(Guid.NewGuid(), seed.Device.Id, other.Site, Instant, Instant, "0000BBB", 1m, null));
            return Task.FromResult(true);
        }, default));
        Assert.Equal(547, SqlNumber(error));
        Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM OpenIddictApplications WHERE Id={seed.Device!.Id.ToString("N")}"))).Number);
        Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM WorkshopAccess WHERE Id={seed.Site}"))).Number);
    }

    [SqlFact]
    public async Task AllOrderedLocksAreHeldAndTimeoutIsClassifiedWithoutExecutingCallback()
    {
        var seed = await SeedAsync(true);
        var eventId = Guid.NewGuid();
        await using var holder = database.CreateContext();
        await using var waiter = database.CreateContext();
        await Uow(holder).ExecuteAsync(new(seed.Actor, seed.Device!.Id, eventId), async ct =>
        {
            foreach (var resource in new[] { "workshops:catalog", "authentication:user:" + seed.Actor.ToString("N"),
                         "lpr:device:" + seed.Device.Id.ToString("N"), "lpr:event:" + eventId.ToString("N") })
                Assert.Equal("Exclusive", await holder.Database.SqlQuery<string>($"SELECT APPLOCK_MODE('public',{resource},'Transaction') AS [Value]").SingleAsync(ct));
            var called = false;
            var error = await Assert.ThrowsAsync<PersistenceFailureException>(() => Uow(waiter, new() { LockTimeoutSeconds = 1, RequestTimeoutSeconds = 3 })
                .ExecuteAsync(new(null, seed.Device.Id), _ => { called = true; return Task.FromResult(true); }, ct));
            Assert.Equal(FailureCode.ServiceUnavailable, error.Code);
            Assert.False(called);
            return true;
        }, default);
    }

    [SqlFact]
    public async Task CancellationAndOverallTimeoutDoNotCommitPendingChanges()
    {
        var seed = await SeedAsync(true);
        await using var db = database.CreateContext();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Uow(db).ExecuteAsync(new(null, seed.Device!.Id), _ => Task.FromResult(true), cancelled.Token));
        var eventId = Guid.NewGuid();
        var failure = await Assert.ThrowsAsync<PersistenceFailureException>(() => Uow(db, new() { LockTimeoutSeconds = 1, RequestTimeoutSeconds = 2 })
            .ExecuteAsync(new(null, seed.Device!.Id), async ct =>
            {
                new LprRepository(db).Add(new LprEvent(eventId, seed.Device.Id, seed.Site, Instant, Instant, "0000BBB", 1m, null));
                await Task.Delay(Timeout.Infinite, ct);
                return true;
            }, default));
        Assert.Equal(FailureCode.ServiceUnavailable, failure.Code);
        Assert.False(await db.LprEvents.AnyAsync(x => x.EventId == eventId));
    }

    [SqlFact]
    public async Task LostCommitAcknowledgmentIsUnknownAndNeverBlindlyRetried()
    {
        var seed = await SeedAsync(true);
        var interceptor = new LostCommit();
        await using var db = new AuthenticationDbContext(new DbContextOptionsBuilder<AuthenticationDbContext>()
            .UseSqlServer(database.ConnectionString).AddInterceptors(interceptor).Options);
        var eventId = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<PersistenceFailureException>(() => Uow(db).ExecuteAsync(new(null, seed.Device!.Id, eventId), ct =>
        {
            new LprRepository(db).Add(new LprEvent(eventId, seed.Device.Id, seed.Site, Instant, Instant, "0000BBB", 1m, null));
            return Task.FromResult(true);
        }, default));
        Assert.Equal(FailureCode.OperationOutcomeUnknown, error.Code);
        Assert.Equal(1, interceptor.Commits);
        await using var verify = database.CreateContext();
        Assert.Equal(1, await verify.LprEvents.CountAsync(x => x.EventId == eventId));
        Assert.Equal(LprEventComparison.Identical, (await new LprRepository(verify).CompareAsync(new(eventId, seed.Device!.Id, seed.Site, Instant, "0000BBB", 1m), default)).Comparison);
    }

    private async Task<(string Site, Guid Actor, LprDevice? Device)> SeedAsync(bool device = false)
    {
        await using var db = database.CreateContext();
        var site = "synthetic-" + Guid.NewGuid().ToString("N");
        var actor = Guid.NewGuid();
        db.Workshops.Add(new() { Id = site, Code = site, Name = "Synthetic workshop", IsActive = true });
        db.Users.Add(new() { Id = actor, DirectoryObjectId = Guid.NewGuid(), Username = "synthetic", DisplayName = "Synthetic" });
        if (!await db.Catalog.AnyAsync()) db.Catalog.Add(new() { Id = 1, LastPublishedAt = Instant });
        LprDevice? result = null;
        if (device)
        {
            result = new(Guid.NewGuid(), "Synthetic device", site, true, Instant);
            db.Set<OpenIddictEntityFrameworkCoreApplication>().Add(Application(result.Id));
            db.LprDevices.Add(result).Property<string>("OAuthApplicationId").CurrentValue = result.Id.ToString("N");
        }
        await db.SaveChangesAsync();
        return (site, actor, result);
    }

    private static OpenIddictEntityFrameworkCoreApplication Application(Guid id) => new()
    { Id = id.ToString("N"), ClientId = "lpr_" + id.ToString("N"), ClientType = OpenIddictConstants.ClientTypes.Confidential };
    private static LprUnitOfWork Uow(AuthenticationDbContext db, LprPersistenceOptions? options = null) =>
        new(db, options ?? new(), NullLogger<LprUnitOfWork>.Instance);
    private static int SqlNumber(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is SqlException sql) return sql.Number;
        throw new InvalidOperationException("Expected a SQL failure.");
    }
    private sealed class LostCommit : DbTransactionInterceptor
    {
        public int Commits { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Commits++;
            throw new IOException("Synthetic lost commit acknowledgment.");
        }
    }
}
