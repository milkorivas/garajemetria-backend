using System.Data;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.History;
using GarajeMetria.Application.Stays;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GarajeMetria.Infrastructure.Persistence;

internal sealed class HistoryStore(AuthenticationDbContext db, IWorkshopCalendar calendar,
    HistoryOptions options, ILogger<HistoryStore> logger) : IHistoryCatalogQuery, IHistoryReadSession,
    IHistoryClosureDiscovery, IHistoryAccessAuditStore
{
    public async Task<string[]?> ResolveAsync(string[] requested, CancellationToken ct)
    {
        if (!await db.Catalog.AsNoTracking().AnyAsync(x => x.Id == 1 && x.LastPublishedAt != null, ct))
            throw new PersistenceFailureException(FailureCode.ServiceUnavailable);
        var ids = await db.Workshops.AsNoTracking().Select(x => x.Id).ToArrayAsync(ct);
        if (requested.Length == 0) return ids;
        return requested.All(x => ids.Contains(x, StringComparer.Ordinal)) ? requested : null;
    }
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        db.Database.SetCommandTimeout(options.SqlTimeoutSeconds);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var result = await action(ct);
            await transaction.CommitAsync(ct);
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw SqlTransactions.Classify(ex); }
        finally { db.ChangeTracker.Clear(); }
    }
    public async Task<IReadOnlyList<StayClosureWork>> ReadDueAsync(HistoryRequest request, HistoryScope scope,
        HistoryWindow? window, DateTimeOffset now, CancellationToken ct)
    {
        if (request.Kind is HistoryKind.Devices or HistoryKind.Locations) return [];
        var partitions = db.VehicleStayPartitions.AsNoTracking()
            .Where(x => EF.Parameter(scope.WorkshopIds).Contains(EF.Functions.Collate(x.WorkshopId, StayMappingRules.Ordinal)) && x.NextClosureAt <= now);
        if (request.Filter.Plate is { } plate)
        {
            var normalized = HistoryValidator.Normalize(plate);
            partitions = partitions.Where(x => request.Filter.PlateMatch == "exact" ?
                EF.Functions.Collate(x.PlateNormalized, StayMappingRules.Ordinal) == normalized :
                EF.Functions.Collate(x.PlateNormalized, StayMappingRules.Ordinal).Contains(normalized));
        }
        if (request.Kind == HistoryKind.Event)
            partitions = partitions.Where(p => db.LprEvents.Any(e => e.EventId == request.ResourceId &&
                e.WorkshopId == p.WorkshopId && e.PlateNormalized == p.PlateNormalized));
        else if (request.Kind is HistoryKind.Stay or HistoryKind.Observations)
            partitions = partitions.Where(p => db.VehicleStays.Any(s => s.Id == request.ResourceId && s.PartitionId == p.Id));
        else if (request.Kind == HistoryKind.Events && window is not null)
            partitions = partitions.Where(p => db.LprEvents.Any(e => e.WorkshopId == p.WorkshopId && e.PlateNormalized == p.PlateNormalized &&
                (request.Filter.TimeBasis == "receipt" ? e.ReceivedAt >= window.Start && e.ReceivedAt < window.End :
                    e.CapturedAt >= window.Start && e.CapturedAt < window.End) &&
                (request.Filter.DeviceIds.Length == 0 || request.Filter.DeviceIds.Contains(e.DeviceId))));
        else if (window is not null)
            partitions = partitions.Where(p => db.VehicleStays.Any(s => s.PartitionId == p.Id && s.AbsorbedByStayId == null &&
                (s.EntryAt == null || s.EntryAt < window.End) && (s.ExitAt == null || s.ExitAt >= window.Start)));
        else if (request.Kind == HistoryKind.Present)
            partitions = partitions.Where(p => db.VehicleStays.Any(s => s.PartitionId == p.Id && s.AbsorbedByStayId == null &&
                s.State == GarajeMetria.Domain.Stays.StayState.Open));
        var rows = await partitions
            .OrderBy(x => x.NextClosureAt).ThenBy(x => x.Id).Take(100)
            .Select(x => new { x.Id, x.NextClosureAt }).ToArrayAsync(ct);
        return rows.Select(x => new StayClosureWork(x.Id, calendar.GetLocalDate(x.NextClosureAt!.Value.AddTicks(-1)), x.NextClosureAt.Value)).ToArray();
    }
    public async Task AppendAsync(HistoryAudit audit, CancellationToken ct)
    {
        await SqlTransactions.ExecuteAsync(db, "history:audit:" + audit.Id.ToString("N"), async token =>
        {
            if (await db.Set<HistoryAccessAuditRow>().AnyAsync(x => x.Id == audit.Id, token)) return true;
            db.Set<HistoryAccessAuditRow>().Add(new()
            {
                Id = audit.Id,
                ActorUserId = audit.Scope.Actor.Id,
                OccurredAt = audit.OccurredAt,
                Operation = audit.Operation,
                ScopeKind = audit.Scope.AllAuthorized ? "AllAuthorized" : "Selected",
                CorrelationId = audit.CorrelationId.Length <= 128 ? audit.CorrelationId : audit.Id.ToString("N"),
                RowCount = audit.RowCount
            });
            db.Set<HistoryAccessAuditWorkshopRow>().AddRange(audit.Scope.WorkshopIds.Distinct(StringComparer.Ordinal)
                .Select(x => new HistoryAccessAuditWorkshopRow { AuditId = audit.Id, WorkshopId = x }));
            return true;
        }, logger, ct);
    }
}
