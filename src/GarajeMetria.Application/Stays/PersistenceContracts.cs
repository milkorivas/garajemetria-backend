using GarajeMetria.Application.Authentication;
using GarajeMetria.Domain.Stays;

namespace GarajeMetria.Application.Stays;

public enum StayProjectionCause { Ingestion, DailyClosure, InitialProjection }
public sealed record StayProjectionWrite(Guid PartitionId, long ExpectedInputRevision, long ExpectedProjectionRevision,
    StayProjectionCause Cause, DateTimeOffset EvaluatedAt, Guid? CausingEventId, DateTimeOffset? NextClosureAt,
    StayRecalculation Changes)
{
    // Operational deadlines for the complete affected context; do not overwrite work outside that context.
    public IReadOnlyDictionary<Guid, DateTimeOffset?> NextClosures { get; init; } = new Dictionary<Guid, DateTimeOffset?>();
}
public sealed record StayProjectionWriteResult(long ProjectionRevision, long LastCreationOrder);
public sealed record StayProjectionPage(IReadOnlyList<StayProjection> Items, long? NextCreationOrder);

public interface IStayRepository
{
    // Called under the catalog + partition coordination in the current use-case transaction.
    Task<StayPartition?> FindPartitionAsync(Guid partitionId, CancellationToken cancellationToken);
    Task<StayPartition> GetOrCreatePartitionAsync(string workshopId, string plateNormalized,
        CancellationToken cancellationToken);
    // Exactly once per newly accepted valid event, within ingestion's existing transaction; retries that
    // match an existing event must not advance input revision. Preserve the saved plate classification.
    Task<StayPartition> RegisterInputAsync(Guid partitionId, Guid eventId, long expectedInputRevision,
        CancellationToken cancellationToken);
    // Return active projections whose evidence or boundaries intersect the window, including estimated/missing
    // boundaries. Always ascend immutable CreationOrder (window.Direction only applies to observations),
    // exclusive cursor; fully materialize historical evidence/policy.
    Task<StayProjectionPage> ReadContextAsync(StayReadWindow window, long? afterCreationOrder,
        CancellationToken cancellationToken);
    // Stage revision, assignments, groups and lineage together; preserve absorbed identities and raw events.
    // Apply only explicit deltas; never delete rows outside the supplied context. No-op creates no revision.
    // Check both revisions and partition membership; conflict aborts the enclosing transaction.
    Task<StayProjectionWriteResult> ApplyAsync(StayProjectionWrite write, CancellationToken cancellationToken);
}

public sealed record ActiveStayPolicy(StayPolicySnapshot Policy, Guid ConfigurationVersion, DateTimeOffset ActivatedAt);
public interface IStayPolicyRepository
{
    // Read current shared values, fail unavailable on absence/mismatch. Write admission callers must hold
    // catalog coordination until commit so activation cannot race the check. Historical Find is independent.
    Task<ActiveStayPolicy> GetActiveAsync(CancellationToken cancellationToken);
    Task<StayPolicySnapshot?> FindAsync(Guid policyId, CancellationToken cancellationToken);
    // Conservative context discovery only; interpretation always uses each full historical snapshot.
    Task<TimeSpan> GetMaximumContextWindowAsync(Guid partitionId, CancellationToken cancellationToken);
    // Explicit deployment operation, not startup mutation. Compare configuration version under coordination;
    // immutable snapshots already referenced by history are never overwritten.
    // Guid.Empty expects the initial, absent configuration. A stale expectation returns false without changes.
    // An identical active policy is a no-op; a shared fingerprint may reuse another immutable policy ID.
    Task<bool> ActivateAsync(StayPolicySnapshot policy, Guid expectedConfigurationVersion,
        DateTimeOffset activatedAt, CancellationToken cancellationToken);
}

public sealed record StayClosureCursor(DateTimeOffset DueAt, Guid PartitionId, DateOnly LocalDate);
public sealed record StayClosureWork(Guid PartitionId, DateOnly LocalDate, DateTimeOffset DueAt);
public sealed record StayClosurePage(IReadOnlyList<StayClosureWork> Items, StayClosureCursor? NextCursor);
public sealed record StayDayClosure(Guid PartitionId, DateOnly LocalDate, long ProcessedInputRevision,
    DateTimeOffset ClosedAt, DateTimeOffset? NextCheckAt);

public interface IStayClosureRepository
{
    // 1..100 items ordered DueAt, canonical UUID bytes, LocalDate; exclusive keyset cursor.
    // Include inactive workshops/devices. Discovery does not claim or mark work as processed.
    Task<StayClosurePage> ReadDueAsync(DateTimeOffset now, int pageSize, StayClosureCursor? after,
        CancellationToken cancellationToken);
    Task<StayDayClosure?> FindAsync(Guid partitionId, DateOnly localDate, CancellationToken cancellationToken);
    // Under the same partition lock/transaction as the projection. A stale input revision is not closed.
    Task RecordAsync(StayDayClosure closure, CancellationToken cancellationToken);
}

public interface IStayClosureUnitOfWork
{
    // Catalog -> persisted partition; no device/user impersonation, nested transaction or external I/O.
    // Share scoped repositories/context and the ingestion partition lock. Commit only successful outcomes;
    // failure, exception, cancellation or revision conflict rolls back effects AND closure progress.
    // Do not blindly retry a write after uncertain commit. Handler rereads partition and shared clock inside.
    Task<Outcome<T>> ExecuteAsync<T>(Guid partitionId,
        Func<CancellationToken, Task<Outcome<T>>> action, CancellationToken cancellationToken);
}
