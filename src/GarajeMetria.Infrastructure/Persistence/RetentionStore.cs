using GarajeMetria.Application.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GarajeMetria.Infrastructure.Persistence;

internal sealed class RetentionStore(AuthenticationDbContext db, AuthenticationOptions options, ILogger<RetentionStore> logger) : IAuthenticationRetentionStore
{
    public Task<CleanupResult> CleanBatchAsync(string owner, RetentionPolicy policy, CancellationToken cancellationToken) =>
        SqlTransactions.ExecuteAsync(db, "authentication:cleanup", async ct =>
        {
            var now = await SqlTransactions.NowAsync(db, ct);
            var schedule = await db.CleanupSchedules.SingleOrDefaultAsync(x => x.Id == 1, ct);
            if (schedule is null)
            {
                schedule = new() { Id = 1 };
                db.CleanupSchedules.Add(schedule);
            }
            if (schedule.NextRunAt > now || schedule.LeaseUntil > now && schedule.Owner != owner)
                return new CleanupResult(false, 0, 0, 0, false);
            schedule.Owner = owner;
            schedule.LeaseUntil = now.AddMinutes(2);
            // The transaction lock fences each batch against every other owner.
            var refreshCutoff = now.AddHours(-policy.RefreshRetentionHoursAfterSessionEnd);
            var sessionCutoff = now.AddDays(-policy.SessionRetentionDays);
            var auditCutoff = now.AddDays(-policy.AuditRetentionDays);
            var refreshIds = await (from token in db.RefreshTokens
                                    join session in db.Sessions on token.SessionId equals session.Id
                                    let end = session.ClosedAt != null && session.ClosedAt < session.AbsoluteExpiresAt
                                        ? session.ClosedAt.Value : session.AbsoluteExpiresAt
                                    where end <= refreshCutoff
                                    orderby end, token.Id
                                    select token.Id).Take(policy.BatchSize).ToArrayAsync(ct);
            var refresh = await db.RefreshTokens.Where(x => refreshIds.Contains(x.Id)).ExecuteDeleteAsync(ct);
            var sessionIds = await db.Sessions
                .Where(x => (x.ClosedAt != null && x.ClosedAt < x.AbsoluteExpiresAt ? x.ClosedAt.Value : x.AbsoluteExpiresAt) <= sessionCutoff &&
                    !db.RefreshTokens.Any(t => t.SessionId == x.Id))
                .OrderBy(x => x.AbsoluteExpiresAt).ThenBy(x => x.Id).Select(x => x.Id).Take(policy.BatchSize).ToArrayAsync(ct);
            var sessions = await db.Sessions.Where(x => sessionIds.Contains(x.Id)).ExecuteDeleteAsync(ct);
            var auditIds = await db.ProvisioningAudits.Where(x => x.OccurredAt <= auditCutoff)
                .OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).Select(x => x.Id).Take(policy.BatchSize).ToArrayAsync(ct);
            var audits = await db.ProvisioningAudits.Where(x => auditIds.Contains(x.Id)).ExecuteDeleteAsync(ct);
            var assignmentIds = await db.AssignmentAudits.Where(x => x.OccurredAt <= auditCutoff)
                .OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).Select(x => x.Id).Take(policy.BatchSize).ToArrayAsync(ct);
            var assignmentAudits = await db.AssignmentAudits.Where(x => assignmentIds.Contains(x.Id)).ExecuteDeleteAsync(ct);
            var more = refresh == policy.BatchSize || sessions == policy.BatchSize || audits == policy.BatchSize || assignmentAudits == policy.BatchSize;
            // Rate event retention is independent of session/audit retention.
            var permitCutoff = now.AddSeconds(-options.RateLimitWindowSeconds);
            var permitIds = await db.RatePermits.Where(x => x.AcceptedAt <= permitCutoff)
                .OrderBy(x => x.AcceptedAt).ThenBy(x => x.Id).Select(x => x.Id).Take(policy.BatchSize).ToArrayAsync(ct);
            await db.RatePermits.Where(x => permitIds.Contains(x.Id)).ExecuteDeleteAsync(ct);
            var partitions = await db.RatePartitions.Where(x => !db.RatePermits.Any(p => p.PartitionId == x.Id))
                .OrderBy(x => x.Id).Select(x => x.Id).Take(policy.BatchSize).ToArrayAsync(ct);
            foreach (var partition in partitions)
            {
                await SqlTransactions.LockAsync(db, "authentication:quota:" + partition, ct);
                await db.RatePartitions.Where(x => x.Id == partition && !db.RatePermits.Any(p => p.PartitionId == x.Id)).ExecuteDeleteAsync(ct);
            }
            more |= permitIds.Length == policy.BatchSize || partitions.Length == policy.BatchSize;
            if (!more)
            {
                schedule.NextRunAt = now.AddHours(policy.CleanupIntervalHours);
                schedule.LeaseUntil = now;
                schedule.Owner = null;
            }
            return new CleanupResult(true, refresh, sessions, audits + assignmentAudits, more);
        }, logger, cancellationToken);

}
