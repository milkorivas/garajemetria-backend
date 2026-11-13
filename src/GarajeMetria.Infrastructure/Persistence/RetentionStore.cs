using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Lpr;
using GarajeMetria.Infrastructure.Lpr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenIddict.EntityFrameworkCore.Models;

namespace GarajeMetria.Infrastructure.Persistence;

internal sealed class RetentionStore(AuthenticationDbContext db, AuthenticationOptions options, ILogger<RetentionStore> logger,
    LprOptions? lpr = null, DeviceOAuthOptions? deviceOAuth = null) : IAuthenticationRetentionStore
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
            var deviceAuditIds = await db.DeviceAdministrationAudits.Where(x => x.OccurredAt <= auditCutoff)
                .OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).Select(x => x.Id).Take(policy.BatchSize).ToArrayAsync(ct);
            var deviceAudits = await db.DeviceAdministrationAudits.Where(x => deviceAuditIds.Contains(x.Id)).ExecuteDeleteAsync(ct);
            // OpenIddict has no revocation timestamp. Expiration plus the margin is a
            // conservative bound, including revoked tokens; unknown dates/states stay intact.
            var tokenCutoff = now.UtcDateTime.AddHours(-(deviceOAuth?.DeviceTokenRetentionHours ?? 24));
            var tokens = db.Set<OpenIddictEntityFrameworkCoreToken>();
            // BIN2 still pads trailing spaces; a terminal marker makes these comparisons exact.
            var deviceTokenIds = await tokens.Where(x => EF.Functions.Collate(x.Type + "#", "Latin1_General_100_BIN2") == "access_token#" &&
                    (EF.Functions.Collate(x.Status + "#", "Latin1_General_100_BIN2") == "valid#" || EF.Functions.Collate(x.Status + "#", "Latin1_General_100_BIN2") == "revoked#") &&
                    x.CreationDate != null && x.CreationDate <= tokenCutoff && x.ExpirationDate != null && x.ExpirationDate <= tokenCutoff &&
                    (x.RedemptionDate == null || x.RedemptionDate <= tokenCutoff) &&
                    db.LprDevices.Any(d => EF.Property<string>(d, "OAuthApplicationId") == x.Application!.Id))
                .OrderBy(x => x.ExpirationDate).ThenBy(x => x.Id).Select(x => x.Id).Take(policy.BatchSize).ToArrayAsync(ct);
            var deviceTokens = await tokens.Where(x => deviceTokenIds.Contains(x.Id)).ExecuteDeleteAsync(ct);
            var more = refresh == policy.BatchSize || sessions == policy.BatchSize || audits == policy.BatchSize || assignmentAudits == policy.BatchSize ||
                deviceAudits == policy.BatchSize || deviceTokens == policy.BatchSize;
            // Rate event retention is independent of session/audit retention.
            var permitCutoff = now.AddSeconds(-Math.Max(options.RateLimitWindowSeconds, lpr?.RateLimitWindowSeconds ?? 0));
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
            if (schedule.LeaseUntil <= await SqlTransactions.NowAsync(db, ct))
                throw new PersistenceFailureException(FailureCode.ServiceUnavailable);
            if (!more)
            {
                schedule.NextRunAt = now.AddHours(policy.CleanupIntervalHours);
                schedule.LeaseUntil = now;
                schedule.Owner = null;
            }
            return new CleanupResult(true, refresh, sessions, audits + assignmentAudits + deviceAudits, more, deviceTokens);
        }, logger, cancellationToken);

}
