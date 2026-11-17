using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Stays;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GarajeMetria.Infrastructure.Persistence;

internal sealed class StayClosureRepository(AuthenticationDbContext db, IWorkshopCalendar calendar) : IStayClosureRepository
{
    public async Task<StayClosurePage> ReadDueAsync(DateTimeOffset now, int pageSize, StayClosureCursor? after, CancellationToken ct)
    {
        if (pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(pageSize));
        var due = after?.DueAt ?? DateTimeOffset.MinValue;
        var key = after?.PartitionId.ToByteArray(bigEndian: true) ?? new byte[16];
        var hasCursor = after is not null;
        var rows = await db.VehicleStayPartitions.FromSqlInterpolated($"SELECT TOP ({pageSize + 1}) * FROM [VehicleStayPartitions] WHERE [NextClosureAt] <= {now} AND ({!hasCursor} = 1 OR [NextClosureAt] > {due} OR ([NextClosureAt] = {due} AND CONVERT(binary(16), REPLACE(CONVERT(char(36), [Id]), '-', ''), 2) > {key})) ORDER BY [NextClosureAt], CONVERT(binary(16), REPLACE(CONVERT(char(36), [Id]), '-', ''), 2)")
            .AsNoTracking().ToArrayAsync(ct);
        var items = rows.Take(pageSize).Select(x => new StayClosureWork(x.Id, calendar.GetLocalDate(x.NextClosureAt!.Value.AddTicks(-1)), x.NextClosureAt.Value)).ToArray();
        return new(items, rows.Length > pageSize ? new(items[^1].DueAt, items[^1].PartitionId, items[^1].LocalDate) : null);
    }

    public async Task<StayDayClosure?> FindAsync(Guid partitionId, DateOnly localDate, CancellationToken ct)
    {
        var row = await db.StayDayClosures.AsNoTracking().SingleOrDefaultAsync(x => x.PartitionId == partitionId && x.LocalDate == localDate, ct);
        return row is null ? null : new(row.PartitionId, row.LocalDate, row.ProcessedInputRevision, row.ClosedAt, row.NextCheckAt);
    }

    public async Task RecordAsync(StayDayClosure closure, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null || calendar.GetDay(closure.LocalDate).End > closure.ClosedAt)
            throw new InvalidOperationException("Invalid stay closure context.");
        var revision = await db.VehicleStayPartitions.Where(x => x.Id == closure.PartitionId).Select(x => x.InputRevision).SingleAsync(ct);
        if (revision != closure.ProcessedInputRevision) throw new PersistenceFailureException(FailureCode.ServiceUnavailable);
        var row = db.StayDayClosures.Local.SingleOrDefault(x => x.PartitionId == closure.PartitionId && x.LocalDate == closure.LocalDate) ??
            await db.StayDayClosures.SingleOrDefaultAsync(x => x.PartitionId == closure.PartitionId && x.LocalDate == closure.LocalDate, ct);
        if (row?.ProcessedInputRevision == revision) return;
        if (row is null)
        {
            row = new() { PartitionId = closure.PartitionId, LocalDate = closure.LocalDate };
            db.StayDayClosures.Add(row);
        }
        row.ProcessedInputRevision = revision; row.ClosedAt = closure.ClosedAt; row.NextCheckAt = closure.NextCheckAt;
    }
}

internal sealed class StayClosureUnitOfWork(AuthenticationDbContext db, LprPersistenceOptions options,
    ILogger<StayClosureUnitOfWork> logger) : IStayClosureUnitOfWork
{
    public async Task<Outcome<T>> ExecuteAsync<T>(Guid partitionId, Func<CancellationToken, Task<Outcome<T>>> action, CancellationToken ct)
    {
        if (partitionId == Guid.Empty || db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Invalid or nested closure transaction.");
        options.Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
        try
        {
            return await SqlTransactions.ExecuteAsync(db, "workshops:catalog", async token =>
            {
                await SqlTransactions.LockAsync(db, "stays:partition:" + partitionId.ToString("N"), token, options.LockTimeoutSeconds * 1000);
                return await action(token);
            }, logger, timeout.Token, options.LockTimeoutSeconds * 1000, rollbackFailedOutcomes: true);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        { throw new PersistenceFailureException(FailureCode.ServiceUnavailable, ex); }
        finally { db.ChangeTracker.Clear(); }
    }
}
