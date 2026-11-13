using System.Globalization;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Lpr;
using GarajeMetria.Infrastructure.Lpr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GarajeMetria.Infrastructure.Persistence;

internal sealed class LprRateLimitStore(AuthenticationDbContext db, LprOptions options, DeviceOAuthOptions oauth,
    DeviceOAuthCertificates certificates, ILprClock clock, ILogger<LprRateLimitStore> logger) : ILprRateLimitStore
{
    internal string Namespace => "lpr:" + RateLimitStore.Hash(options.RateLimitNamespace);
    private string Fingerprint => RateLimitStore.Hash(string.Join(':', new[] { options.MaxEventAgeMinutes, options.MaxFutureMinutes,
        options.EventsPerDevice, options.EventsPerIp, options.AdminWritesPerUser, options.RateLimitWindowSeconds,
        options.TokensPerClient, options.TokensPerIp, oauth.AccessTokenMinutes, oauth.DeviceTokenRetentionHours,
        options.RequestTimeoutSeconds, options.LockTimeoutSeconds }
        .Select(x => x.ToString(CultureInfo.InvariantCulture))) + "|" + oauth.Issuer + "|" + oauth.Audience + "|" +
        certificates.Signing.Thumbprint + "|" + certificates.Encryption.Thumbprint);

    public async Task<RateDecision> AdmitAsync(RatePolicy policy, string subject, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
        try { return await AdmitCoreAsync(policy, subject, timeout.Token); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        { throw new PersistenceFailureException(FailureCode.ServiceUnavailable, ex); }
    }

    private async Task<RateDecision> AdmitCoreAsync(RatePolicy policy, string subject, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null) throw new InvalidOperationException("LPR quotas must precede business transactions.");
        if (policy != options.Policy(policy.Name)) throw new InvalidOperationException("Unknown or inconsistent LPR quota.");
        // Verify before every admission, independently of RF-001 availability/AD and its policy allowlist.
        await SqlTransactions.ExecuteAsync(db, "lpr:policy:" + Namespace, async token =>
        {
            var current = await db.RateConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == Namespace, token);
            if (current is null) db.RateConfigurations.Add(new() { Id = Namespace, Fingerprint = Fingerprint });
            else if (current.Fingerprint != Fingerprint) throw new PersistenceFailureException(FailureCode.ServiceUnavailable);
            return true;
        }, logger, ct, options.LockTimeoutSeconds * 1000);
        var key = RateLimitStore.Hash(Namespace + "\n" + policy.Name + "\n" + subject);
        // The shared legacy tables' cleanup uses this lock prefix. Hash namespaces/policies remain distinct.
        return await SqlTransactions.ExecuteAsync(db, "authentication:quota:" + key, async token =>
        {
            var now = await clock.GetUtcNowAsync(token);
            var cutoff = now.AddSeconds(-policy.WindowSeconds);
            if (!await db.RatePartitions.AnyAsync(x => x.Id == key, token)) db.RatePartitions.Add(new() { Id = key });
            await db.RatePermits.Where(x => x.PartitionId == key && x.AcceptedAt <= cutoff).ExecuteDeleteAsync(token);
            var times = await db.RatePermits.AsNoTracking().Where(x => x.PartitionId == key && x.AcceptedAt > cutoff && x.AcceptedAt <= now)
                .OrderBy(x => x.AcceptedAt).Select(x => x.AcceptedAt).ToArrayAsync(token);
            if (times.Length >= policy.Limit) return new RateDecision(false, now, times[times.Length - policy.Limit].AddSeconds(policy.WindowSeconds));
            db.RatePermits.Add(new() { Id = Guid.NewGuid(), PartitionId = key, AcceptedAt = now });
            return new RateDecision(true, now, null);
        }, logger, ct, options.LockTimeoutSeconds * 1000);
    }
}
