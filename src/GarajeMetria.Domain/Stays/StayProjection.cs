namespace GarajeMetria.Domain.Stays;

// Immutable input/output for reconciliation; it never exposes a mutable aggregate.
public sealed class StayProjection
{
    public StayProjection(Guid id, Guid originEventId, DateTimeOffset createdAt, long creationOrder,
        StayCandidate candidate, DateTimeOffset? updatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var origin = candidate.Observations.SingleOrDefault(o => o.EventId == originEventId) ??
            throw new ArgumentException("The origin must belong to the projection.");
        _ = new VehicleStay(id, origin, createdAt, creationOrder, candidate.Policy, candidate.Entry, candidate.Exit);
        if (updatedAt < createdAt)
            throw new ArgumentException("A projection update cannot precede creation.");
        Id = id;
        OriginEventId = originEventId;
        CreatedAt = createdAt.ToUniversalTime();
        CreationOrder = creationOrder;
        Candidate = candidate;
        UpdatedAt = (updatedAt ?? createdAt).ToUniversalTime();
    }

    public Guid Id { get; }
    public Guid OriginEventId { get; }
    public DateTimeOffset CreatedAt { get; }
    public long CreationOrder { get; }
    public DateTimeOffset UpdatedAt { get; }
    public StayCandidate Candidate { get; }
    public override string ToString() => nameof(StayProjection);
}

public sealed record StayIdentityAllocation
{
    public StayIdentityAllocation(Guid id, long creationOrder)
    {
        if (id == Guid.Empty || creationOrder <= 0)
            throw new ArgumentException("Invalid stay identity allocation.");
        Id = id;
        CreationOrder = creationOrder;
    }

    public Guid Id { get; }
    public long CreationOrder { get; }
}
