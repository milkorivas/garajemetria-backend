using GarajeMetria.Application.History;
using GarajeMetria.Application.Users;
using GarajeMetria.Domain.Lpr;
using GarajeMetria.Infrastructure.History;
using GarajeMetria.Infrastructure.Persistence;
using GarajeMetria.Infrastructure.Stays;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;

namespace GarajeMetria.IntegrationTests;

public sealed class HistoryPersistenceTests(SqlFixture database) : IClassFixture<SqlFixture>
{
    [SqlFact]
    public async Task CaptureReceiptAndOriginalTextAreIndependentAndLocationRemainsHistorical()
    {
        var seed = await SeedAsync();
        await using var db = database.CreateContext();
        var old = Guid.NewGuid(); var current = Guid.NewGuid();
        db.LprDeviceLocations.AddRange(new LprDeviceLocationRow
        {
            Id = old,
            DeviceId = seed.Device,
            WorkshopId = seed.Site,
            Description = "Old access",
            RecordedAt = seed.Now.AddDays(-3),
            CreationOrder = 1
        },
            new LprDeviceLocationRow
            {
                Id = current,
                DeviceId = seed.Device,
                WorkshopId = seed.Site,
                Description = "New access",
                RecordedAt = seed.Now.AddDays(-1),
                CreationOrder = 2
            });
        db.LprObservationLocations.Add(new()
        {
            EventId = seed.Event,
            DeviceId = seed.Device,
            WorkshopId = seed.Site,
            LocationId = old,
            Provenance = GarajeMetria.Application.Stays.ObservationLocationProvenance.AtCapture
        });
        await db.SaveChangesAsync();
        var scope = Scope(seed.Site);
        var session = Store(db);
        var window = new HistoryWindow(seed.Now.AddHours(-1), seed.Now.AddHours(1));
        var q = new HistoryQuery(db);
        var capture = await session.ExecuteAsync(ct => q.ReadAsync(new SearchLprEventsQuery(new()), scope, window, seed.Now, false, ct), default);
        Assert.Equal(0, capture.Value!.TotalCount);
        var receipt = await session.ExecuteAsync(ct => q.ReadAsync(new SearchLprEventsQuery(new() { TimeBasis = "receipt" }), scope, window, seed.Now, false, ct), default);
        Assert.Equal(1, receipt.Value!.TotalCount);
        Assert.Equal("Old access", Assert.Single(receipt.Value.Items).Event!.Location.Description);
        window = new(seed.Now.AddYears(-1), seed.Now.AddSeconds(1));
        var literal = await session.ExecuteAsync(ct => q.ReadAsync(new SearchLprEventsQuery(new() { OriginalText = "bad %_ " }), scope, window, seed.Now, false, ct), default);
        Assert.Equal(1, literal.Value!.TotalCount);
        literal = await session.ExecuteAsync(ct => q.ReadAsync(new SearchLprEventsQuery(new() { OriginalText = "bad %_" }), scope, window, seed.Now, false, ct), default);
        Assert.Equal(0, literal.Value!.TotalCount);
    }

    [SqlFact]
    public async Task SerializableReadPreventsPhantomBetweenCountAndRows()
    {
        var seed = await SeedAsync();
        await using var reader = database.CreateContext();
        await using var writer = database.CreateContext();
        await writer.Database.OpenConnectionAsync();
        var next = Event(seed.Site, seed.Device, seed.Now, seed.Now, Guid.NewGuid());
        await Store(reader).ExecuteAsync(async ct =>
        {
            var before = await reader.LprEvents.CountAsync(x => x.WorkshopId == seed.Site, ct);
            await writer.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT 100", ct);
            writer.LprEvents.Add(next);
            var error = await Assert.ThrowsAnyAsync<Exception>(() => writer.SaveChangesAsync(ct));
            Assert.Equal(1222, Assert.IsType<SqlException>(error.GetBaseException()).Number);
            Assert.Equal(before, await reader.LprEvents.CountAsync(x => x.WorkshopId == seed.Site, ct));
            return true;
        }, default);
        await writer.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT 15000");
        await writer.SaveChangesAsync();
        Assert.Equal(2, await reader.LprEvents.CountAsync(x => x.WorkshopId == seed.Site));
    }

    [SqlFact]
    public async Task UpgradeFromRf004PreservesEventsAndDownRefusesToDeleteAccessAudit()
    {
        var isolated = new SqlFixture();
        try
        {
            await isolated.InitializeAsync();
            await using var db = isolated.CreateContext();
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261117125011_StayPersistence");
            var legacy = await new HistoryPersistenceTests(isolated).SeedAsync();
            await migrator.MigrateAsync();
            var saved = await db.LprEvents.AsNoTracking().SingleAsync(x => x.EventId == legacy.Event);
            Assert.Equal("bad %_ ", saved.PlateOriginal);
            Assert.Equal(legacy.Now, saved.ReceivedAt);
            Assert.Equal(legacy.Event.ToByteArray(bigEndian: true), saved.EventOrderKey);
            var user = new LocalUser { Id = Guid.NewGuid(), DirectoryObjectId = Guid.NewGuid(), Username = "synthetic", DisplayName = "Synthetic" };
            db.Users.Add(user);
            db.Set<HistoryAccessAuditRow>().Add(new()
            {
                Id = Guid.NewGuid(),
                ActorUserId = user.Id,
                OccurredAt = DateTimeOffset.UtcNow,
                Operation = "ReadEvents",
                ScopeKind = "AllAuthorized",
                CorrelationId = "synthetic",
                RowCount = 0
            });
            await db.SaveChangesAsync();
            await Assert.ThrowsAnyAsync<Exception>(() => migrator.MigrateAsync("20261117125011_StayPersistence"));
            Assert.Equal(1, await db.Set<HistoryAccessAuditRow>().CountAsync());
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }
        finally { await isolated.DisposeAsync(); }
    }
    private HistoryStore Store(AuthenticationDbContext db) => new(db, new WorkshopCalendar(), new(), NullLogger<HistoryStore>.Instance);
    private static HistoryScope Scope(string site) => new(new CurrentUser(Guid.NewGuid(), Guid.NewGuid(), "administrator"), [site], false);
    private async Task<(string Site, Guid Device, Guid Event, DateTimeOffset Now)> SeedAsync()
    {
        var site = "history-sql-" + Guid.NewGuid().ToString("N");
        var device = Guid.NewGuid(); var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await using var db = database.CreateContext();
        db.Workshops.Add(new() { Id = site, Code = site, Name = "Synthetic", IsActive = true });
        db.Set<OpenIddictEntityFrameworkCoreApplication>().Add(new() { Id = device.ToString("N"), ClientId = "lpr_" + device.ToString("N"), ClientType = "confidential" });
        db.LprDevices.Add(new(device, "Synthetic", site, true, now)).Property<string>("OAuthApplicationId").CurrentValue = device.ToString("N");
        db.LprEvents.Add(Event(site, device, now.AddDays(-2), now, id));
        await db.SaveChangesAsync();
        return (site, device, id, now);
    }
    private static LprEventRow Event(string site, Guid device, DateTimeOffset capture, DateTimeOffset receipt, Guid id) => new()
    {
        EventId = id,
        DeviceId = device,
        WorkshopId = site,
        CapturedAt = capture,
        ReceivedAt = receipt,
        PlateOriginal = "bad %_ ",
        PlateNormalized = "BAD%_",
        PlateStatus = "invalid",
        PlateRulesVersion = "es-v1",
        Confidence = 0
    };
}
