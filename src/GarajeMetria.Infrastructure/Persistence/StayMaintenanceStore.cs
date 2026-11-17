using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Stays;
using GarajeMetria.Domain.Stays;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GarajeMetria.Infrastructure.Persistence;

internal sealed class StayMaintenanceStore(AuthenticationDbContext db, LprPersistenceOptions options,
    ILogger<StayMaintenanceStore> logger) : IStayMaintenanceStore
{
    public async Task<StayInitialProjectionPage> ReadPendingAsync(int pageSize, CancellationToken ct)
    {
        if (pageSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(pageSize));
        var ids = await db.LprEvents.AsNoTracking().Where(e => !db.LprObservationLocations.Any(l => l.EventId == e.EventId) ||
            e.PlateStatus == "valid" && !db.StayObservationAssignments.Any(a => a.EventId == e.EventId))
            .OrderBy(e => e.CapturedAt).ThenBy(e => e.EventOrderKey).Take(pageSize).Select(e => e.EventId).ToArrayAsync(ct);
        return new(ids);
    }

    public async Task<StayObservation?> FindObservationAsync(Guid eventId, CancellationToken ct)
    {
        var row = await db.LprEvents.AsNoTracking().SingleOrDefaultAsync(e => e.EventId == eventId, ct);
        return row is null ? null : StayRepository.Observation(row);
    }
    public Task<bool> IsProjectedAsync(Guid eventId, CancellationToken ct) =>
        db.StayObservationAssignments.AnyAsync(a => a.EventId == eventId, ct);

    public async Task<Outcome<T>> ExecuteAsync<T>(Func<CancellationToken, Task<Outcome<T>>> action, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null) throw new InvalidOperationException("Maintenance cannot nest a transaction.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
        try
        {
            return await SqlTransactions.ExecuteAsync(db, "workshops:catalog", action, logger, timeout.Token,
            options.LockTimeoutSeconds * 1000, rollbackFailedOutcomes: true);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        { throw new PersistenceFailureException(FailureCode.ServiceUnavailable, ex); }
        finally { db.ChangeTracker.Clear(); }
    }
}

internal sealed class StayDiagnostics(AuthenticationDbContext db, IStayPolicyRepository policies,
    GarajeMetria.Application.Lpr.ILprClock clock) : IStayDiagnostics
{
    public async Task<StayDiagnosticResult> ReadAsync(CancellationToken ct)
    {
        try
        {
            var now = await clock.GetUtcNowAsync(ct);
            var due = db.VehicleStayPartitions.AsNoTracking().Where(x => x.NextClosureAt <= now);
            var count = await due.LongCountAsync(ct);
            var oldest = await due.Select(x => x.NextClosureAt).MinAsync(ct);
            await db.Database.ExecuteSqlRawAsync("""
                SELECT TOP (0) [Id], [PolicyId], [Revision] FROM [VehicleStays];
                SELECT TOP (0) [PartitionId], [Revision], [ChangesJson] FROM [StayProjectionRevisions];
                SELECT TOP (0) [EventId], [GroupOriginEventId] FROM [StayObservationAssignments];
                SELECT TOP (0) [SourceStayId], [TargetStayId] FROM [StayLineage];
                SELECT TOP (0) [PartitionId], [ProcessedInputRevision] FROM [StayDayClosures];
                SELECT TOP (0) [Id], [CreationOrder] FROM [LprDeviceLocations];
                SELECT TOP (0) [EventId], [LocationId] FROM [LprObservationLocations];
                SELECT TOP (0) [EventOrderKey] FROM [LprEvents];
                """, ct);
            var matches = true;
            try { await policies.GetActiveAsync(ct); }
            catch (PersistenceFailureException) { matches = false; }
            return new(true, matches, count, oldest is null ? null : now - oldest.Value);
        }
        catch (Exception ex) when (ex is PersistenceFailureException or SqlException)
        { return new(false, false, 0, null); }
    }
}
