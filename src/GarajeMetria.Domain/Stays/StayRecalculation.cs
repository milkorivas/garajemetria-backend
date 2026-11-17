namespace GarajeMetria.Domain.Stays;

public enum StayLineageKind { Fusion, Split }

public sealed record StayLineage
{
    public StayLineage(Guid sourceId, Guid targetId, StayLineageKind kind)
    {
        if (sourceId == Guid.Empty || targetId == Guid.Empty || sourceId == targetId || !Enum.IsDefined(kind))
            throw new ArgumentException("Invalid stay lineage.");
        SourceId = sourceId;
        TargetId = targetId;
        Kind = kind;
    }

    public Guid SourceId { get; }
    public Guid TargetId { get; }
    public StayLineageKind Kind { get; }
}

public sealed class StayRecalculation
{
    internal StayRecalculation(IEnumerable<StayProjection> active, IEnumerable<StayProjection> created,
        IEnumerable<StayProjection> updated, IEnumerable<StayProjection> absorbed, IEnumerable<StayLineage> lineage)
    {
        Active = Array.AsReadOnly(active.ToArray());
        Created = Array.AsReadOnly(created.ToArray());
        Updated = Array.AsReadOnly(updated.ToArray());
        Absorbed = Array.AsReadOnly(absorbed.ToArray());
        AddedLineage = Array.AsReadOnly(lineage.ToArray());
    }

    public IReadOnlyList<StayProjection> Active { get; }
    public IReadOnlyList<StayProjection> Created { get; }
    public IReadOnlyList<StayProjection> Updated { get; }
    public IReadOnlyList<StayProjection> Absorbed { get; }
    public IReadOnlyList<StayLineage> AddedLineage { get; }
    public bool HasChanges => Created.Count + Updated.Count + Absorbed.Count != 0;

    public StayRecalculation KeepRelevant(Func<StayProjection, bool> relevant)
    {
        var active = Active.Where(relevant).ToArray();
        var ids = active.Select(p => p.Id).ToHashSet();
        if (Updated.Any(p => !ids.Contains(p.Id)) || AddedLineage.Any(l => !ids.Contains(l.TargetId)))
            throw new ArgumentException("A changed historical component cannot be truncated.");
        return new(active, Created.Where(p => ids.Contains(p.Id)), Updated, Absorbed, AddedLineage);
    }
}
