using GarajeMetria.Infrastructure.Workshops;
using Microsoft.EntityFrameworkCore;

namespace GarajeMetria.IntegrationTests;

public sealed class SqlWorkshopSourceTests(SqlFixture database) : IClassFixture<SqlFixture>
{
    [SqlFact]
    public async Task ReadIncludesOnlyActiveWorkshopsAndConfirmsAnEmptyResult()
    {
        await using var setup = database.CreateContext();
        await setup.Database.ExecuteSqlRawAsync("CREATE TABLE dbo.Workshops (Code nvarchar(12) NOT NULL PRIMARY KEY, Name nvarchar(256) NOT NULL, IsActive bit NOT NULL)");
        await setup.Database.ExecuteSqlRawAsync("CREATE VIEW dbo.ActiveWorkshops AS SELECT Code, Name FROM dbo.Workshops WHERE IsActive = 1");
        await setup.Database.ExecuteSqlRawAsync("INSERT dbo.Workshops VALUES ('000001', N'Active example', 1), ('000002', N'Inactive example', 0)");
        await using var sourceDb = new WorkshopSourceDbContext(new DbContextOptionsBuilder<WorkshopSourceDbContext>()
            .UseSqlServer(database.ConnectionString).Options);
        var source = new SqlWorkshopSource(sourceDb);
        var read = await source.ReadAsync(default);
        Assert.True(read.IsComplete);
        Assert.Equal("000001", Assert.Single(read.Items).Code);
        Assert.Empty(sourceDb.ChangeTracker.Entries());
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ReadAsync(cancelled.Token));
        }
        await setup.Database.ExecuteSqlRawAsync("UPDATE dbo.Workshops SET IsActive = 0");
        read = await source.ReadAsync(default);
        Assert.True(read.IsComplete);
        Assert.True(read.EmptyConfirmed);
        Assert.Empty(read.Items);
        await Assert.ThrowsAsync<NotSupportedException>(() => sourceDb.SaveChangesAsync());
    }

    [SqlFact]
    public async Task FailedReadNeverReturnsACompleteSnapshot()
    {
        await using var sourceDb = new WorkshopSourceDbContext(new DbContextOptionsBuilder<WorkshopSourceDbContext>()
            .UseSqlServer(database.ConnectionString).Options);
        // This class shares a test database: an absent view must fail rather than return an empty catalog.
        await sourceDb.Database.ExecuteSqlRawAsync("DROP VIEW IF EXISTS dbo.ActiveWorkshops");
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => new SqlWorkshopSource(sourceDb).ReadAsync(default));
    }
}
