using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Stays;
using GarajeMetria.Domain.Stays;
using GarajeMetria.Infrastructure.Stays;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GarajeMetria.Infrastructure.Persistence;

internal sealed class StayPolicyRepository(AuthenticationDbContext db, StayOptions options,
    LprPersistenceOptions persistence, ILogger<StayPolicyRepository> logger) : IStayPolicyRepository
{
    public async Task<ActiveStayPolicy> GetActiveAsync(CancellationToken cancellationToken)
    {
        var active = await (from configuration in db.StayConfiguration.AsNoTracking()
                            join policy in db.StayPolicies.AsNoTracking() on configuration.ActivePolicyId equals policy.Id
                            where configuration.Id == 1
                            select new { Configuration = configuration, Policy = policy }).SingleOrDefaultAsync(cancellationToken);
        if (active is null)
        {
            logger.LogWarning("The shared stay policy has not been activated.");
            throw Unavailable();
        }
        var snapshot = Snapshot(active.Policy);
        if (!options.Matches(snapshot))
        {
            logger.LogWarning("The shared stay policy differs from this instance configuration.");
            throw Unavailable();
        }
        return new(snapshot, active.Configuration.Version, active.Configuration.ActivatedAt);
    }

    public async Task<StayPolicySnapshot?> FindAsync(Guid policyId, CancellationToken cancellationToken)
    {
        if (policyId == Guid.Empty) throw new ArgumentException("A policy identifier is required.", nameof(policyId));
        var row = await db.StayPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.Id == policyId, cancellationToken);
        // Historical lookup does not compare with the active node configuration or reinterpret its values.
        return row is null ? null : Snapshot(row);
    }

    public async Task<TimeSpan> GetMaximumContextWindowAsync(Guid partitionId, CancellationToken ct)
    {
        if (partitionId == Guid.Empty) throw new ArgumentException("A partition identifier is required.", nameof(partitionId));
        var maxima = await db.StayPolicies.AsNoTracking().Where(p =>
            db.StayConfiguration.Any(c => c.ActivePolicyId == p.Id) ||
            db.VehicleStays.Any(s => s.PartitionId == partitionId && s.AbsorbedByStayId == null && s.PolicyId == p.Id))
            .GroupBy(_ => 1).Select(g => new
            {
                X = g.Max(x => x.EstimatedDurationTicks),
                R = g.Max(x => x.RepeatWindowTicks),
                M = g.Max(x => x.MaximumAssociationTicks)
            }).SingleOrDefaultAsync(ct);
        if (maxima is null) throw Unavailable();
        return TimeSpan.FromTicks(Math.Max(maxima.X, Math.Max(maxima.R, maxima.M)));
    }

    public async Task<bool> ActivateAsync(StayPolicySnapshot policy, Guid expectedConfigurationVersion,
        DateTimeOffset activatedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!options.Matches(policy) || activatedAt.Offset != TimeSpan.Zero || db.Database.CurrentTransaction is not null ||
            db.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Invalid stay policy activation context.");
        persistence.Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(persistence.RequestTimeoutSeconds));
        try
        {
            // Same first lock as ingestion/closure; readers checking the policy under it cannot race activation.
            return await SqlTransactions.ExecuteAsync(db, "workshops:catalog", async ct =>
            {
                var current = await db.StayConfiguration.SingleOrDefaultAsync(x => x.Id == 1, ct);
                if (current is null ? expectedConfigurationVersion != Guid.Empty : current.Version != expectedConfigurationVersion)
                    return false;
                var fingerprint = StayOptions.Fingerprint(policy);
                var existing = await db.StayPolicies.SingleOrDefaultAsync(x => x.Id == policy.Id, ct);
                if (existing is not null && Snapshot(existing) != policy)
                    throw new InvalidOperationException("An immutable stay policy cannot be replaced.");
                existing ??= await db.StayPolicies.SingleOrDefaultAsync(x => x.Fingerprint == fingerprint, ct);
                if (existing is null)
                {
                    existing = new()
                    {
                        Id = policy.Id,
                        Fingerprint = fingerprint,
                        EstimatedDurationTicks = policy.EstimatedDuration.Ticks,
                        RepeatWindowTicks = policy.RepeatWindow.Ticks,
                        MaximumAssociationTicks = policy.MaximumAssociationDuration.Ticks,
                        TimeZoneId = policy.TimeZoneId,
                        AlgorithmVersion = policy.AlgorithmVersion
                    };
                    db.StayPolicies.Add(existing);
                }
                else if (StayOptions.Fingerprint(Snapshot(existing)) != fingerprint)
                    throw Unavailable();
                if (current?.ActivePolicyId == existing.Id) return true;
                if (current is not null && activatedAt < current.ActivatedAt)
                    throw new InvalidOperationException("Stay policy activation time cannot move backwards.");
                if (current is null)
                    db.StayConfiguration.Add(new() { Id = 1, ActivePolicyId = existing.Id, Version = Guid.NewGuid(), ActivatedAt = activatedAt });
                else
                {
                    current.ActivePolicyId = existing.Id;
                    current.Version = Guid.NewGuid();
                    current.ActivatedAt = activatedAt;
                }
                return true;
            }, logger, timeout.Token, persistence.LockTimeoutSeconds * 1000);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        { throw new PersistenceFailureException(FailureCode.ServiceUnavailable, ex); }
        finally { db.ChangeTracker.Clear(); }
    }

    internal static StayPolicySnapshot Snapshot(StayPolicyRow row)
    {
        try
        {
            var snapshot = new StayPolicySnapshot(row.Id, TimeSpan.FromTicks(row.EstimatedDurationTicks),
                TimeSpan.FromTicks(row.RepeatWindowTicks), TimeSpan.FromTicks(row.MaximumAssociationTicks), row.TimeZoneId, row.AlgorithmVersion);
            if (StayOptions.Fingerprint(snapshot) != row.Fingerprint) throw Unavailable();
            return snapshot;
        }
        catch (ArgumentException ex) { throw new PersistenceFailureException(FailureCode.ServiceUnavailable, ex); }
    }

    private static PersistenceFailureException Unavailable() => new(FailureCode.ServiceUnavailable);
}
