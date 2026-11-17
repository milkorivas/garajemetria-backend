using GarajeMetria.Application;
using GarajeMetria.Application.Stays;
using GarajeMetria.Infrastructure;
using GarajeMetria.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GarajeMetria.IntegrationTests;

public sealed class StayMigrationTests
{
    [SqlFact]
    public async Task UpgradeFromRf003PreservesCredentialsEventsAndAuditsAndComputesNetworkOrder()
    {
        var fixture = new SqlFixture();
        try
        {
            await fixture.InitializeAsync();
            await using var db = fixture.CreateContext();
            await db.StayConfiguration.ExecuteDeleteAsync(); await db.StayPolicies.ExecuteDeleteAsync();
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261113115955_LprPersistence");
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:Authentication"] = fixture.ConnectionString }).Build();
            var services = new ServiceCollection(); services.AddApplication(); services.AddStayMaintenance(configuration);
            using (var provider = services.BuildServiceProvider())
            using (var scope = provider.CreateScope())
            {
                var diagnostic = await scope.ServiceProvider.GetRequiredService<IStayDiagnostics>().ReadAsync(default);
                Assert.False(diagnostic.SchemaAvailable); Assert.False(diagnostic.PolicyMatches);
                Assert.Equal(0, diagnostic.DuePartitions); Assert.Null(diagnostic.OldestPendingAge);
            }
            var device = Guid.NewGuid(); var actor = Guid.NewGuid(); var eventId = Guid.Parse("11223344-5566-4788-99aa-bbccddeeff00");
            var version = Guid.NewGuid(); var authorization = Guid.NewGuid(); var audit = Guid.NewGuid();
            var now = new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero).AddTicks(1234567);
            var client = device.ToString("N");
            db.Workshops.Add(new() { Id = "legacy-stay", Code = "legacy-stay", Name = "Synthetic", IsActive = true });
            db.Users.Add(new() { Id = actor, DirectoryObjectId = Guid.NewGuid(), Username = "synthetic", DisplayName = "Synthetic" });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [OpenIddictApplications] ([Id], [ClientId], [ClientSecret]) VALUES ({client}, {"lpr_" + client}, {"synthetic-hash-only"})");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [LprDevices] ([Id],[Name],[WorkshopId],[IsActive],[HasCredentials],[Version],[AuthorizationVersion],[CreatedAt],[UpdatedAt],[OAuthApplicationId]) VALUES ({device},N'Synthetic',N'legacy-stay',1,1,{version},{authorization},{now},{now},{client})");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [LprEvents] ([EventId],[DeviceId],[WorkshopId],[CapturedAt],[ReceivedAt],[PlateOriginal],[PlateNormalized],[PlateStatus],[PlateFormat],[PlateRulesVersion],[Confidence],[Direction]) VALUES ({eventId},{device},N'legacy-stay',{now},{now},N'synthetic-invalid',N'SYNTHETICINVALID','invalid',NULL,'saved-v0',0,NULL)");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [DeviceAdministrationAudits] ([Id],[ActorId],[DeviceId],[Operation],[OccurredAt],[CorrelationId],[CurrentDeviceId],[CurrentWorkshopId],[CurrentName],[CurrentIsActive],[CurrentHasCredentials],[CurrentVersion],[CurrentAuthorizationVersion],[CurrentCreatedAt],[CurrentUpdatedAt]) VALUES ({audit},{actor},{device},'Created',{now},N'synthetic',{device},N'legacy-stay',N'Synthetic',1,1,{version},{authorization},{now},{now})");
            await migrator.MigrateAsync();
            var row = await db.LprEvents.AsNoTracking().SingleAsync(x => x.EventId == eventId);
            Assert.Equal(eventId.ToByteArray(bigEndian: true), row.EventOrderKey);
            Assert.Equal("saved-v0", row.PlateRulesVersion); Assert.Equal("invalid", row.PlateStatus);
            Assert.Equal(now, row.CapturedAt); Assert.Equal(now, row.ReceivedAt);
            Assert.Equal("synthetic-invalid", row.PlateOriginal);
            var stored = await db.LprDevices.AsNoTracking().SingleAsync(x => x.Id == device);
            Assert.Null(stored.CurrentLocationId); Assert.Equal(version, stored.Version); Assert.Equal(authorization, stored.AuthorizationVersion);
            Assert.Equal("synthetic-hash-only", await db.Database.SqlQuery<string>($"SELECT [ClientSecret] AS [Value] FROM [OpenIddictApplications] WHERE [Id] = {client}").SingleAsync());
            Assert.Equal(audit, (await db.DeviceAdministrationAudits.AsNoTracking().SingleAsync()).Id);
            Assert.Empty(await db.LprObservationLocations.ToArrayAsync());
            Assert.Empty(await db.LprDeviceLocations.ToArrayAsync());
            var cascades = await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.foreign_keys WHERE delete_referential_action <> 0").SingleAsync();
            Assert.Equal(0, cascades);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            // An unused RF-004 schema can be removed/reapplied without touching the preserved RF-003 rows.
            await migrator.MigrateAsync("20261113115955_LprPersistence"); await migrator.MigrateAsync();
            Assert.Equal(eventId, (await db.LprEvents.AsNoTracking().SingleAsync()).EventId);
        }
        finally { await fixture.DisposeAsync(); }
    }

    [SqlFact]
    public async Task DowngradeRefusesToDeleteActivatedPolicyHistory()
    {
        var fixture = new SqlFixture();
        try
        {
            await fixture.InitializeAsync();
            await using var db = fixture.CreateContext();
            var policy = await db.StayPolicies.AsNoTracking().SingleAsync();
            await Assert.ThrowsAnyAsync<Exception>(() => db.GetService<IMigrator>().MigrateAsync("20261113115955_LprPersistence"));
            db.ChangeTracker.Clear();
            Assert.Equal(policy.Id, (await db.StayPolicies.AsNoTracking().SingleAsync()).Id);
            Assert.Contains("20261117125011_StayPersistence", await db.Database.GetAppliedMigrationsAsync());
        }
        finally { await fixture.DisposeAsync(); }
    }
}
