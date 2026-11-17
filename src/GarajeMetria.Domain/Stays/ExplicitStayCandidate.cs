namespace GarajeMetria.Domain.Stays;

// Calculation evidence, not a persisted identity. Identity/lineage reconciliation is separate.
public sealed class ExplicitStayCandidate
{
    internal ExplicitStayCandidate(StayBoundary? entry, StayBoundary? exit,
        StayPolicySnapshot policy, IEnumerable<ObservationGroup> groups)
    {
        Entry = entry;
        Exit = exit;
        Policy = policy;
        Groups = Array.AsReadOnly(groups.ToArray());
        Observations = Array.AsReadOnly(Groups.SelectMany(g => g.Members).Order(StayObservationOrder.Instance).ToArray());
    }

    public StayBoundary? Entry { get; }
    public StayBoundary? Exit { get; }
    public StayPolicySnapshot Policy { get; }
    public IReadOnlyList<ObservationGroup> Groups { get; }
    public IReadOnlyList<StayObservation> Observations { get; }
    public StayState State => Entry is null ? StayState.PendingEntry : Exit is null ? StayState.Open : StayState.ClosedProvisional;
    public TimeSpan? Duration => Entry is not null && Exit is not null ? Exit.At - Entry.At : null;
    public override string ToString() => nameof(ExplicitStayCandidate);
}
