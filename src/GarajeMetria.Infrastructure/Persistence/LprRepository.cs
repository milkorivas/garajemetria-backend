using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Lpr;
using GarajeMetria.Domain.Lpr;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GarajeMetria.Infrastructure.Persistence;

public sealed class LprPersistenceOptions
{
    public int RequestTimeoutSeconds { get; set; } = 15;
    public int LockTimeoutSeconds { get; set; } = 5;

    internal void Validate()
    {
        if (RequestTimeoutSeconds is <= 0 or > int.MaxValue / 1000 || LockTimeoutSeconds <= 0 || LockTimeoutSeconds >= RequestTimeoutSeconds)
            throw new InvalidOperationException("Invalid LPR persistence timeouts.");
    }
}

internal sealed class LprUnitOfWork(AuthenticationDbContext db, LprPersistenceOptions options, ILogger<LprUnitOfWork> logger) : ILprUnitOfWork
{
    public async Task<T> ExecuteAsync<T>(LprTransactionScope scope, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        options.Validate();
        if (scope.DeviceId == Guid.Empty || scope.AdministratorId == Guid.Empty || scope.EventId == Guid.Empty || db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Invalid or nested LPR transaction.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
        try
        {
            return await SqlTransactions.ExecuteAsync(db, "workshops:catalog", async ct =>
            {
                if (scope.AdministratorId is { } actor)
                    await SqlTransactions.LockAsync(db, "authentication:user:" + actor.ToString("N"), ct, options.LockTimeoutSeconds * 1000);
                await SqlTransactions.LockAsync(db, "lpr:device:" + scope.DeviceId.ToString("N"), ct, options.LockTimeoutSeconds * 1000);
                if (scope.EventId is { } eventId)
                    await SqlTransactions.LockAsync(db, "lpr:event:" + eventId.ToString("N"), ct, options.LockTimeoutSeconds * 1000);
                return await action(ct);
            }, logger, timeout.Token, options.LockTimeoutSeconds * 1000, rollbackFailedOutcomes: true);
        }
        catch (PersistenceFailureException ex) when (ex.Code == FailureCode.InternalError && scope.EventId is { } eventId &&
            db.ChangeTracker.Entries<LprEventRow>().Any(x => x.State == EntityState.Added && x.Entity.EventId == eventId) && IsEventKeyCollision(ex))
        { throw new LprEventIdentityCollisionException(ex); }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        { throw new GarajeMetria.Application.Authentication.PersistenceFailureException(GarajeMetria.Application.Authentication.FailureCode.ServiceUnavailable, ex); }
        finally { db.ChangeTracker.Clear(); }
    }

    private static bool IsEventKeyCollision(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is SqlException sql)
                return sql.Errors.Cast<SqlError>().Any(x => x.Number == 2627 && x.Message.Contains("'PK_LprEvents'", StringComparison.Ordinal));
        return false;
    }
}

internal sealed class LprClock(AuthenticationDbContext db) : ILprClock
{
    public Task<DateTimeOffset> GetUtcNowAsync(CancellationToken cancellationToken) => SqlTransactions.NowAsync(db, cancellationToken);
}

internal sealed class LprRepository(AuthenticationDbContext db) : ILprDeviceRepository, ILprEventRepository,
    ILprDeviceAdministrationQuery, IDeviceAuthorizationQuery, IDeviceAdministrationAuditRepository
{
    // Stable store key, distinct from the public client_id (lpr_<UUID>). The credential adapter must use this ID.
    internal static string ApplicationId(Guid deviceId) => deviceId.ToString("N");

    public Task<LprDevice?> FindAsync(Guid deviceId, CancellationToken cancellationToken) =>
        db.LprDevices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deviceId, cancellationToken);

    public void Add(LprDevice device)
    {
        RequireTransaction();
        db.LprDevices.Add(device).Property<string>("OAuthApplicationId").CurrentValue = ApplicationId(device.Id);
    }

    public Task<bool> SaveAsync(LprDevice device, Guid expectedVersion, CancellationToken cancellationToken)
    {
        RequireTransaction();
        return SaveCoreAsync(device, expectedVersion, cancellationToken);
    }

    private async Task<bool> SaveCoreAsync(LprDevice device, Guid expectedVersion, CancellationToken cancellationToken) =>
        await db.LprDevices.Where(x => x.Id == device.Id && x.WorkshopId == device.WorkshopId && x.Version == expectedVersion)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.Name, device.Name)
                .SetProperty(x => x.IsActive, device.IsActive).SetProperty(x => x.HasCredentials, device.HasCredentials)
                .SetProperty(x => x.Version, device.Version).SetProperty(x => x.AuthorizationVersion, device.AuthorizationVersion)
                .SetProperty(x => x.UpdatedAt, device.UpdatedAt), cancellationToken) == 1;

    public void Add(LprEvent observation)
    {
        RequireTransaction();
        db.LprEvents.Add(new()
        {
            EventId = observation.EventId,
            DeviceId = observation.DeviceId,
            WorkshopId = observation.WorkshopId,
            CapturedAt = observation.CapturedAt,
            ReceivedAt = observation.ReceivedAt,
            PlateOriginal = observation.PlateOriginal,
            PlateNormalized = observation.PlateNormalized,
            PlateStatus = observation.PlateStatus,
            PlateFormat = observation.PlateFormat,
            PlateRulesVersion = observation.PlateRulesVersion,
            Confidence = observation.Confidence,
            Direction = observation.Direction
        });
    }

    public async Task<LprEventLookup> CompareAsync(ReceiveLprEventCommand incoming, CancellationToken cancellationToken)
    {
        var row = await db.LprEvents.AsNoTracking().Where(x => x.EventId == incoming.EventId)
            .Select(x => new
            {
                Observation = new LprObservation(x.EventId, x.DeviceId, x.WorkshopId, x.CapturedAt,
                x.PlateOriginal, x.Confidence, x.Direction),
                x.ReceivedAt,
                x.PlateStatus
            }).SingleOrDefaultAsync(cancellationToken);
        if (row is null) return new(LprEventComparison.Missing);
        var candidate = new LprObservation(incoming.EventId, incoming.DeviceId, incoming.WorkshopId,
            incoming.CapturedAt, incoming.PlateOriginal, incoming.Confidence, incoming.Direction);
        return row.Observation.Matches(candidate) ? new(LprEventComparison.Identical, new(incoming.EventId, row.ReceivedAt, row.PlateStatus)) :
            new(LprEventComparison.Conflict);
    }

    public void Add(DeviceAdministrationAuditEntry entry)
    {
        RequireTransaction();
        db.DeviceAdministrationAudits.Add(new()
        {
            Id = entry.Id,
            ActorId = entry.ActorId,
            DeviceId = entry.DeviceId,
            Operation = entry.Operation,
            OccurredAt = entry.OccurredAt,
            CorrelationId = entry.CorrelationId,
            Previous = entry.Previous,
            Current = entry.Current
        });
    }

    public async Task<DevicePage> ListAsync(int pageNumber, int pageSize, string? workshopId, bool? isActive, CancellationToken cancellationToken)
    {
        if (pageNumber <= 0 || pageSize is <= 0 or > 100 || (long)(pageNumber - 1) * pageSize > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        var query = db.LprDevices.AsNoTracking();
        if (workshopId is not null) query = query.Where(x => x.WorkshopId == workshopId);
        if (isActive is not null) query = query.Where(x => x.IsActive == isActive);
        var count = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(x => x.Id).Skip((pageNumber - 1) * pageSize).Take(pageSize)
            .Select(x => new DeviceDto(x.Id, x.Name, x.WorkshopId, x.IsActive, x.HasCredentials, x.CreatedAt, x.UpdatedAt, x.Version))
            .ToArrayAsync(cancellationToken);
        return new(items, pageNumber, pageSize, count);
    }

    Task<DeviceDto?> ILprDeviceAdministrationQuery.FindAsync(Guid deviceId, CancellationToken cancellationToken) =>
        db.LprDevices.AsNoTracking().Where(x => x.Id == deviceId)
            .Select(x => new DeviceDto(x.Id, x.Name, x.WorkshopId, x.IsActive, x.HasCredentials, x.CreatedAt, x.UpdatedAt, x.Version))
            .SingleOrDefaultAsync(cancellationToken);

    Task<DeviceAuthorizationState?> IDeviceAuthorizationQuery.FindAsync(Guid deviceId, CancellationToken cancellationToken) =>
        (from device in db.LprDevices.AsNoTracking()
         join workshop in db.Workshops.AsNoTracking() on device.WorkshopId equals workshop.Id
         where device.Id == deviceId
         select new DeviceAuthorizationState(device.Id, device.WorkshopId, device.IsActive, device.HasCredentials,
             device.AuthorizationVersion, db.Catalog.Any(x => x.Id == 1 && x.LastPublishedAt != null), workshop.IsActive))
        .SingleOrDefaultAsync(cancellationToken);

    private void RequireTransaction()
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("LPR writes require a transaction.");
    }
}
