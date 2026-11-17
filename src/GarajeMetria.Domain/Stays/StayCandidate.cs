namespace GarajeMetria.Domain.Stays;

// Complete interpretation evidence; persistent identity is reconciled separately.
public sealed class StayCandidate
{
    public static StayCandidate Restore(StayBoundary? entry, StayBoundary? exit, StayPolicySnapshot policy,
        IEnumerable<ObservationGroup> groups, IEnumerable<UnknownObservationAssociation> associations)
    {
        var candidate = new StayCandidate(entry, exit, policy, groups, associations);
        if (candidate.Observations.Count == 0 || candidate.Observations.Select(x => x.EventId).Distinct().Count() != candidate.Observations.Count ||
            candidate.UnknownAssociations.Any(x => !candidate.Observations.Contains(x.Observation)) ||
            entry?.Observation is { } observedEntry && !candidate.Observations.Contains(observedEntry) ||
            exit?.Observation is { } observedExit && !candidate.Observations.Contains(observedExit) ||
            candidate.UnknownAssociations.Select(x => x.Observation.EventId).Distinct().Count() != candidate.UnknownAssociations.Count ||
            candidate.UnknownAssociations.Count != candidate.Observations.Count(x => x.Direction is null))
            throw new ArgumentException("Invalid saved stay evidence.");
        _ = new VehicleStay(candidate.Observations[0].EventId, candidate.Observations[0], DateTimeOffset.UnixEpoch, 1, policy, entry, exit);
        return candidate;
    }
    internal StayCandidate(StayBoundary? entry, StayBoundary? exit, StayPolicySnapshot policy,
        IEnumerable<ObservationGroup> groups, IEnumerable<UnknownObservationAssociation> associations)
    {
        Entry = entry;
        Exit = exit;
        Policy = policy;
        Groups = Array.AsReadOnly(groups.OrderBy(g => g.EntryRepresentative, StayObservationOrder.Instance).ToArray());
        Observations = Array.AsReadOnly(Groups.SelectMany(g => g.Members).Order(StayObservationOrder.Instance).ToArray());
        UnknownAssociations = Array.AsReadOnly(associations.OrderBy(a => a.Observation, StayObservationOrder.Instance).ToArray());
    }

    public StayBoundary? Entry { get; }
    public StayBoundary? Exit { get; }
    public StayPolicySnapshot Policy { get; }
    public IReadOnlyList<ObservationGroup> Groups { get; }
    public IReadOnlyList<StayObservation> Observations { get; }
    public IReadOnlyList<UnknownObservationAssociation> UnknownAssociations { get; }
    public StayState State => Entry is null ? StayState.PendingEntry : Exit is null ? StayState.Open : StayState.ClosedProvisional;
    public TimeSpan? Duration => Entry is not null && Exit is not null ? Exit.At - Entry.At : null;
    public override string ToString() => nameof(StayCandidate);
}
