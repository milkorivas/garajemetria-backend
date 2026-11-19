using GarajeMetria.Application.Authentication;
using GarajeMetria.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GarajeMetria.IntegrationTests;

public sealed class HistoryConcurrencyTests(SqlFixture database) : IClassFixture<SqlFixture>
{
    [SqlFact]
    public async Task SharedExportLeaseRejectsAnotherHostAndOldOwnerCannotReleaseSuccessor()
    {
        await using var firstDb = database.CreateContext();
        await using var secondDb = database.CreateContext();
        var user = Guid.NewGuid();
        var first = new HistoryAdmission(firstDb, new HistoryInstanceGate(), NullLogger<HistoryAdmission>.Instance);
        var second = new HistoryAdmission(secondDb, new HistoryInstanceGate(), NullLogger<HistoryAdmission>.Instance);
        var permit = await first.AcquireAsync(user, true, default);
        Assert.True(permit.Succeeded);
        Assert.Equal(FailureCode.RateLimited, (await second.AcquireAsync(user, true, default)).Error?.Code);
        await secondDb.Set<HistoryExportLeaseRow>().Where(x => x.UserId == user)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        var successor = await second.AcquireAsync(user, true, default);
        Assert.True(successor.Succeeded);
        await permit.Value!.DisposeAsync();
        Assert.True(await firstDb.Set<HistoryExportLeaseRow>().AnyAsync(x => x.UserId == user));
        await successor.Value!.DisposeAsync();
        Assert.False(await secondDb.Set<HistoryExportLeaseRow>().AnyAsync(x => x.UserId == user));
    }

    [SqlFact]
    public async Task InstanceGateAndSlidingQuotaAreBoundedAndReleasePermits()
    {
        var gate = new HistoryInstanceGate();
        await using var db = database.CreateContext();
        var store = new HistoryAdmission(db, gate, NullLogger<HistoryAdmission>.Instance);
        var one = await store.AcquireAsync(Guid.NewGuid(), true, default);
        var two = await store.AcquireAsync(Guid.NewGuid(), true, default);
        Assert.True(one.Succeeded && two.Succeeded);
        Assert.Equal(FailureCode.RateLimited, (await store.AcquireAsync(Guid.NewGuid(), true, default)).Error?.Code);
        await one.Value!.DisposeAsync(); await two.Value!.DisposeAsync();
        var user = Guid.NewGuid();
        for (var i = 0; i < 60; i++)
        {
            var permit = await store.AcquireAsync(user, false, default);
            Assert.True(permit.Succeeded);
            await permit.Value!.DisposeAsync();
        }
        Assert.Equal(FailureCode.RateLimited, (await store.AcquireAsync(user, false, default)).Error?.Code);
    }
}
