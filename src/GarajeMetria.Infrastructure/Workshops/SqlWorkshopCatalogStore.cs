using System.Data;
using GarajeMetria.Application.Workshops;
using GarajeMetria.Domain.Workshops;
using GarajeMetria.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GarajeMetria.Infrastructure.Workshops;

public sealed class SqlWorkshopCatalogStore(AuthenticationDbContext db, ILogger<SqlWorkshopCatalogStore> logger)
    : IWorkshopCatalogStore
{
    public Task<CatalogObservation> BeginObservationAsync(CancellationToken cancellationToken) =>
        SqlTransactions.ExecuteAsync(db, "workshops:catalog", async ct =>
        {
            var state = await db.Catalog.SingleOrDefaultAsync(x => x.Id == 1, ct);
            if (state is null) { state = new() { Id = 1 }; db.Catalog.Add(state); }
            state.NextObservationVersion = checked(state.NextObservationVersion + 1);
            return new CatalogObservation(state.NextObservationVersion, await SqlTransactions.NowAsync(db, ct));
        }, logger, cancellationToken);

    public Task<CatalogPublication> PublishAsync(CatalogObservation observation, IReadOnlyList<WorkshopEntry> entries,
        CancellationToken cancellationToken)
    {
        // SQL Server ignores trailing spaces even with BIN2; reject this unsupported source shape explicitly.
        if (!WorkshopCatalog.IsValid(entries) || entries.Any(x => x.Code.Length > 256 || x.Name.Length > 2048 ||
            x.Code.EndsWith(' ')))
            throw new InvalidOperationException("Workshop data is incompatible with the configured local mapping.");
        return SqlTransactions.ExecuteAsync(db, "workshops:catalog", async ct =>
        {
            var state = await db.Catalog.SingleAsync(x => x.Id == 1, ct);
            if (observation.Version <= state.PublishedVersion) return new CatalogPublication(false, state.LastPublishedAt);
            if (observation.Version > state.NextObservationVersion) throw new InvalidOperationException("Unknown catalog observation.");
            var now = await SqlTransactions.NowAsync(db, ct);
            var existing = await db.Workshops.ToListAsync(ct);
            var plan = WorkshopCatalog.Reconcile(existing.Select(x => new WorkshopState(x.Id, x.Code, x.Name, x.IsActive)).ToArray(), entries);
            var byId = existing.ToDictionary(x => x.Id, StringComparer.Ordinal);
            foreach (var entry in plan.Added)
            {
                db.Workshops.Add(new() { Id = Guid.NewGuid().ToString("N"), Code = entry.Code, Name = entry.Name, IsActive = true });
            }
            foreach (var entry in plan.Updated)
            {
                var workshop = byId[entry.Id];
                workshop.Name = entry.Name;
                workshop.IsActive = entry.IsActive;
            }
            var ids = plan.DeactivatedIds.ToArray();
            var affected = await db.Sessions.Where(s => s.ClosedAt == null && s.Role == WorkshopCatalog.DeactivatedSessionRole &&
                db.Users.Any(u => u.Id == s.UserId && ids.Contains(u.DefaultWorkshopId!))).ToListAsync(ct);
            foreach (var session in affected) session.Close(now, WorkshopCatalog.DeactivatedSessionReason);
            state.PublishedVersion = observation.Version;
            state.LastObservedAt = now;
            state.LastPublishedAt = now;
            return new CatalogPublication(true, now, plan.Added.Count, plan.Changed, ids.Length, plan.Reactivated);
        }, logger, cancellationToken);
    }

    public Task<DateTimeOffset?> LastPublishedAsync(CancellationToken cancellationToken) =>
        db.Catalog.AsNoTracking().Where(x => x.Id == 1).Select(x => x.LastPublishedAt).SingleOrDefaultAsync(cancellationToken);

    public async Task<WorkshopPage?> ListAsync(int pageNumber, int pageSize, bool? isActive, CancellationToken cancellationToken)
    {
        // Count and page must describe the same committed publication.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await SqlTransactions.LockAsync(db, "workshops:catalog", cancellationToken);
        if (await LastPublishedAsync(cancellationToken) is null) return null;
        var query = db.Workshops.AsNoTracking().Where(x => isActive == null || x.IsActive == isActive);
        var count = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(x => x.Id).Skip((pageNumber - 1) * pageSize).Take(pageSize)
            .Select(x => new WorkshopDto(x.Id, x.Code, x.Name, x.IsActive)).ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(items, pageNumber, pageSize, count);
    }
}
