using System.Security.Cryptography;
using System.Text;
using GarajeMetria.Application.Authentication;
using Microsoft.EntityFrameworkCore;

namespace GarajeMetria.Infrastructure.Persistence;

internal sealed class RateLimitStore(AuthenticationDbContext db, AuthenticationOptions options) : IAuthenticationRateLimitStore
{
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static string Fingerprint(AuthenticationOptions o) => Hash(string.Join(':', o.LoginIpLimit, o.LoginUserLimit,
        o.RefreshIpLimit, o.RefreshUserLimit, o.RateLimitWindowSeconds));

    public Task<bool> VerifyConfigurationAsync(CancellationToken cancellationToken) =>
        SqlTransactions.ExecuteAsync(db, "authentication:policy:" + Hash(options.RateLimitNamespace), async ct =>
        {
            var fingerprint = Fingerprint(options);
            var current = await db.RateConfigurations.SingleOrDefaultAsync(x => x.Id == options.RateLimitNamespace, ct);
            if (current is null) db.RateConfigurations.Add(new() { Id = options.RateLimitNamespace, Fingerprint = fingerprint });
            else if (current.Fingerprint != fingerprint) throw new InvalidOperationException("Rate policy configuration differs from the shared policy.");
            return true;
        }, cancellationToken);

    public async Task<RateDecision> AdmitAsync(RatePolicy policy, string subject, CancellationToken cancellationToken)
    {
        if (policy != options.Policy(policy.Name)) throw new InvalidOperationException("The requested quota differs from configured policy.");
        var key = Hash(options.RateLimitNamespace + "\n" + policy.Name + "\n" + subject);
        return await SqlTransactions.ExecuteAsync(db, "authentication:quota:" + key, async ct =>
        {
            var current = await db.RateConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == options.RateLimitNamespace, ct);
            if (current?.Fingerprint != Fingerprint(options)) throw new InvalidOperationException("Shared rate configuration is unavailable or inconsistent.");
            var now = await SqlTransactions.NowAsync(db, ct);
            var cutoff = now.AddSeconds(-policy.WindowSeconds);
            var partition = await db.RatePartitions.SingleOrDefaultAsync(x => x.Id == key, ct);
            if (partition is null) db.RatePartitions.Add(new() { Id = key });
            // Exact (t-W,t] window. Expired events never affect admission.
            await db.RatePermits.Where(x => x.PartitionId == key && x.AcceptedAt <= cutoff).ExecuteDeleteAsync(ct);
            var times = await db.RatePermits.AsNoTracking().Where(x => x.PartitionId == key && x.AcceptedAt > cutoff && x.AcceptedAt <= now)
                .OrderBy(x => x.AcceptedAt).Select(x => x.AcceptedAt).ToListAsync(ct);
            if (times.Count >= policy.Limit)
                return new RateDecision(false, now, times[times.Count - policy.Limit].AddSeconds(policy.WindowSeconds));
            db.RatePermits.Add(new() { Id = Guid.NewGuid(), PartitionId = key, AcceptedAt = now });
            return new RateDecision(true, now, null);
        }, cancellationToken);
    }
}
