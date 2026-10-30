using GarajeMetria.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace GarajeMetria.IntegrationTests;

public sealed class WorkshopAssignmentMigrationTests(SqlFixture database) : IClassFixture<SqlFixture>
{
    [SqlFact]
    public async Task RF002_CP014_UpgradePreservesProfilesAssignmentsAndProvisioningWithDistinctVersions()
    {
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        // This fixture owns a fresh synthetic database. Exercise the actual pre-RF-002 schema.
        await migrator.MigrateAsync("20261029143036_WorkshopCatalog");
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var directoryFirst = Guid.NewGuid();
        var directorySecond = Guid.NewGuid();
        var audit = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO WorkshopAccess (Id, Code, Name, IsActive) VALUES (N'migration-workshop', N'001', N'Synthetic', 1)");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO LocalUsers (Id, DirectoryObjectId, Username, DisplayName, DefaultWorkshopId) VALUES ({first}, {directoryFirst}, N'first', N'First', N'migration-workshop'), ({second}, {directorySecond}, N'second', N'Second', NULL)");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ProvisioningAudit (Id, UserId, OccurredAt) VALUES ({audit}, {first}, {now})");
        await migrator.MigrateAsync();
        var users = await db.Users.AsNoTracking().ToArrayAsync();
        Assert.Equal(2, users.Length);
        Assert.Equal("migration-workshop", users.Single(x => x.Id == first).DefaultWorkshopId);
        Assert.Null(users.Single(x => x.Id == second).DefaultWorkshopId);
        Assert.Equal(directoryFirst, users.Single(x => x.Id == first).DirectoryObjectId);
        Assert.Equal(2, users.Select(x => x.AssignmentVersion).Distinct().Count());
        Assert.All(users, user => Assert.NotEqual(Guid.Empty, user.AssignmentVersion));
        Assert.True(await db.ProvisioningAudits.AnyAsync(x => x.Id == audit && x.UserId == first));
    }
}
