using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.History;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GarajeMetria.Infrastructure.Persistence;

internal sealed class HistoryInstanceGate
{
    internal SemaphoreSlim Exports { get; } = new(2, 2);
}

internal sealed class HistoryAdmission(AuthenticationDbContext db, HistoryInstanceGate gate, ILogger<HistoryAdmission> logger)
    : IHistoryAdmission
{
    public async Task<Outcome<IAsyncDisposable>> AcquireAsync(Guid userId, bool export, CancellationToken ct)
    {
        var owner = Guid.NewGuid();
        if (export && !await gate.Exports.WaitAsync(0, ct)) return Outcome<IAsyncDisposable>.Fail(FailureCode.RateLimited, 1);
        try
        {
            var admitted = await SqlTransactions.ExecuteAsync(db, "history:admission:" + userId.ToString("N"), async token =>
            {
                var now = await SqlTransactions.NowAsync(db, token);
                var key = RateLimitStore.Hash("history:v1:" + (export ? "export:" : "query:") + userId.ToString("N"));
                if (!await db.RatePartitions.AnyAsync(x => x.Id == key, token)) db.RatePartitions.Add(new() { Id = key });
                await db.RatePermits.Where(x => x.PartitionId == key && x.AcceptedAt <= now.AddMinutes(-1)).ExecuteDeleteAsync(token);
                var count = await db.RatePermits.CountAsync(x => x.PartitionId == key && x.AcceptedAt > now.AddMinutes(-1) && x.AcceptedAt <= now, token);
                if (count >= (export ? 5 : 60)) return false;
                if (export)
                {
                    var lease = await db.Set<HistoryExportLeaseRow>().SingleOrDefaultAsync(x => x.UserId == userId, token);
                    if (lease is not null && lease.ExpiresAt > now) return false;
                    if (lease is null) db.Set<HistoryExportLeaseRow>().Add(new() { UserId = userId, Owner = owner, ExpiresAt = now.AddSeconds(65) });
                    else { lease.Owner = owner; lease.ExpiresAt = now.AddSeconds(65); }
                }
                db.RatePermits.Add(new() { Id = Guid.NewGuid(), PartitionId = key, AcceptedAt = now });
                return true;
            }, logger, ct);
            if (admitted) return Outcome<IAsyncDisposable>.Success(new Permit(this, userId, owner, export));
            if (export) gate.Exports.Release();
            return Outcome<IAsyncDisposable>.Fail(FailureCode.RateLimited, 60);
        }
        catch { if (export) gate.Exports.Release(); throw; }
    }
    private async Task ReleaseAsync(Guid userId, Guid owner)
    {
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await db.Set<HistoryExportLeaseRow>().Where(x => x.UserId == userId && x.Owner == owner).ExecuteDeleteAsync(budget.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or PersistenceFailureException or Microsoft.Data.SqlClient.SqlException)
        { logger.LogWarning("History export lease release deferred to expiration; category={Category}", ex.GetType().Name); }
        finally { gate.Exports.Release(); }
    }
    private sealed class Permit(HistoryAdmission store, Guid userId, Guid owner, bool export) : IAsyncDisposable
    {
        private bool disposed;
        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            if (!export) return;
            await store.ReleaseAsync(userId, owner);
        }
    }
}
