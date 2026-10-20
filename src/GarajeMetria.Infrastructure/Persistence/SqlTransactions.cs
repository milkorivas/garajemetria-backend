using System.Data;
using System.Diagnostics;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Domain.Authentication;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace GarajeMetria.Infrastructure.Persistence;

internal static class SqlTransactions
{
    public static async Task LockAsync(AuthenticationDbContext db, string resource, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource={resource}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000; IF @result < 0 THROW 51000, 'Authentication lock unavailable', 1;", cancellationToken);
    }

    public static async Task<DateTimeOffset> NowAsync(AuthenticationDbContext db, CancellationToken cancellationToken) =>
        await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]")
            .SingleAsync(cancellationToken);

    public static async Task<T> ExecuteAsync<T>(AuthenticationDbContext db, string resource,
        Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        IDbContextTransaction? transaction = null;
        var committing = false;
        try
        {
            transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            await LockAsync(db, resource, cancellationToken);
            db.ChangeTracker.Clear();
            var result = await action(cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            committing = true;
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            var rollbackConfirmed = false;
            if (transaction is not null && !committing)
            {
                try { await transaction.RollbackAsync(CancellationToken.None); rollbackConfirmed = true; }
                catch (Exception rollbackError) when (rollbackError is SqlException or InvalidOperationException) { }
            }
            if (ex is OperationCanceledException && !committing && (transaction is null || rollbackConfirmed)) throw;
            if (committing || transaction is not null && !rollbackConfirmed) throw new PersistenceFailureException(FailureCode.OperationOutcomeUnknown, ex);
            if (ex is PersistenceFailureException) throw;
            throw Classify(ex);
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    public static PersistenceFailureException Classify(Exception exception)
    {
        SqlException? sql = null;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PersistenceFailureException known) return known;
            if (current is SqlException sqlError) { sql = sqlError; break; }
        }
        var transient = sql is not null && sql.Errors.Cast<SqlError>().Any(x =>
            x.Number is -2 or 2 or 20 or 53 or 64 or 233 or 258 or 1205 or 1222 or 4060 or 10053 or 10054 or 10060 or 40197 or 40501 or 40613 or 51000);
        return new(transient ? FailureCode.ServiceUnavailable : FailureCode.InternalError, exception);
    }
}

internal sealed class AuthenticationUnitOfWork(AuthenticationDbContext db, ILogger<AuthenticationUnitOfWork> logger) : IUnitOfWork
{
    public async Task<T> ExecuteAsync<T>(Guid userId, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        string[] closures = [];
        var result = await SqlTransactions.ExecuteAsync(db, "authentication:user:" + userId.ToString("N"), async ct =>
        {
            var value = await action(ct);
            closures = db.ChangeTracker.Entries<AuthenticationSession>()
                .Where(entry => entry.Entity.ClosedAt is not null && entry.Property(x => x.ClosedAt).OriginalValue is null)
                .Select(entry => entry.Entity.CloseReason!).ToArray();
            return value;
        }, cancellationToken);
        foreach (var reason in closures)
            logger.LogInformation("Authentication session closed: reason={Reason}, trace={TraceId}", reason, Activity.Current?.TraceId.ToString());
        return result;
    }
}
