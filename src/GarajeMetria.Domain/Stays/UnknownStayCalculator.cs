using GarajeMetria.Domain.Lpr;

namespace GarajeMetria.Domain.Stays;

public static class UnknownStayCalculator
{
    public static IReadOnlyList<StayCandidate> Calculate(IEnumerable<ExplicitStayCandidate> explicitCandidates,
        IEnumerable<ObservationGroup> unknownGroups, IReadOnlyList<StayDayWindow> days, DateTimeOffset now,
        Func<StayObservation, StayPolicySnapshot> policyForFallbackOrigin)
    {
        ArgumentNullException.ThrowIfNull(explicitCandidates);
        ArgumentNullException.ThrowIfNull(unknownGroups);
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(policyForFallbackOrigin);
        var calendar = days.OrderBy(d => d.Start).ToArray();
        if (calendar.Select(d => d.Date).Distinct().Count() != calendar.Length ||
            calendar.Zip(calendar.Skip(1)).Any(pair => pair.First.End > pair.Second.Start))
            throw new ArgumentException("Stay day windows must have unique dates and cannot overlap.");

        var bases = explicitCandidates.ToArray();
        var groups = unknownGroups.OrderBy(g => g.EntryRepresentative, StayObservationOrder.Instance).ToArray();
        if (groups.Any(g => g.Direction is not null))
            throw new ArgumentException("Unknown calculation requires groups without a declared direction.");
        var observations = bases.SelectMany(c => c.Observations).Concat(groups.SelectMany(g => g.Members)).ToArray();
        var origin = observations.FirstOrDefault();
        if (origin is null) return Array.Empty<StayCandidate>();
        if (observations.Any(o => !string.Equals(o.WorkshopId, origin.WorkshopId, StringComparison.Ordinal) ||
                !string.Equals(o.PlateNormalized, origin.PlateNormalized, StringComparison.Ordinal)) ||
            observations.Select(o => o.EventId).Distinct().Count() != observations.Length)
            throw new ArgumentException("Calculation requires unique events from one vehicle and workshop.");

        var assigned = bases.Select(_ => new List<UnknownObservationAssociation>()).ToArray();
        var fallback = new List<UnknownObservationAssociation>();
        var movements = bases.SelectMany((candidate, index) => candidate.Groups.Select(group =>
            (Candidate: index, Movement: group.Direction == LprDirection.Entry ? group.EntryRepresentative : group.ExitRepresentative))).ToArray();

        foreach (var group in groups)
        {
            foreach (var observation in group.Members)
            {
                // Eligibility uses the explicit basis, never a boundary changed by
                // another unknown reading earlier in the iteration.
                var eligible = movements.Where(m => Eligible(bases[m.Candidate], m.Movement, observation))
                    .OrderBy(m => (m.Movement.CapturedAt - observation.CapturedAt).Duration())
                    .ThenBy(m => m.Movement, StayObservationOrder.Instance).ToArray();
                if (eligible.Length == 0)
                    fallback.Add(new(observation, group, null));
                else
                    assigned[eligible[0].Candidate].Add(new(observation, group, eligible[0].Movement));
            }
        }

        var result = bases.Select((candidate, index) => Expand(candidate, assigned[index])).ToList();
        // Keep the original repeat group, including a group crossing midnight.
        // Only its unmatched members participate in the daily fallback.
        var fallbackGroups = fallback.GroupBy(a => a.OriginalGroup).Select(g => g.ToArray()).ToArray();
        foreach (var dayGroups in fallbackGroups.GroupBy(g => DayAt(g[0].Observation.CapturedAt)))
        {
            var members = dayGroups.SelectMany(g => g).OrderBy(a => a.Observation, StayObservationOrder.Instance).ToArray();
            var first = members[0].Observation;
            var policy = policyForFallbackOrigin(first) ?? throw new ArgumentException("Fallback requires a policy.");
            var entry = StayBoundary.Observed(first, StayBoundarySource.Inferred);
            var exit = dayGroups.Count() > 1
                ? StayBoundary.Observed(members[^1].Observation, StayBoundarySource.Inferred)
                : now >= dayGroups.Key.End ? StayBoundary.Estimated(entry.At + policy.EstimatedDuration) : null;
            result.Add(new(entry, exit, policy, Slices(members), members));
        }

        return Array.AsReadOnly(result.OrderBy(c => c.Observations[0], StayObservationOrder.Instance).ToArray());

        StayDayWindow DayAt(DateTimeOffset at) => calendar.FirstOrDefault(d => d.Contains(at)) ??
            throw new ArgumentException("Calendar bounds are missing for a fallback group.");
    }

    private static bool Eligible(ExplicitStayCandidate candidate, StayObservation movement, StayObservation unknown) =>
        (movement.CapturedAt - unknown.CapturedAt).Duration() <= candidate.Policy.MaximumAssociationDuration &&
        (candidate.Entry is null || (unknown.CapturedAt - candidate.Entry.At).Duration() <= candidate.Policy.MaximumAssociationDuration);

    private static StayCandidate Expand(ExplicitStayCandidate candidate, List<UnknownObservationAssociation> associations)
    {
        var entry = candidate.Entry?.Source == StayBoundarySource.Estimated ? null : candidate.Entry;
        var exit = candidate.Exit?.Source == StayBoundarySource.Estimated ? null : candidate.Exit;
        if (associations.Count > 0)
        {
            var ordered = associations.OrderBy(a => a.Observation, StayObservationOrder.Instance).ToArray();
            var first = ordered[0].Observation;
            var last = ordered[^1].Observation;
            // Observed evidence takes precedence over an estimated boundary. Ties
            // retain the existing support and its explicit/inferred provenance.
            var observedStart = entry?.At ?? exit!.At;
            var observedEnd = exit?.At ?? entry!.At;
            if (first.CapturedAt < observedStart)
                entry = StayBoundary.Observed(first, StayBoundarySource.Inferred);
            if (last.CapturedAt > observedEnd)
                exit = StayBoundary.Observed(last, StayBoundarySource.Inferred);
        }

        if (candidate.Entry?.Source == StayBoundarySource.Estimated && entry is null)
            entry = StayBoundary.Estimated(exit!.At - candidate.Policy.EstimatedDuration);
        if (candidate.Exit?.Source == StayBoundarySource.Estimated && exit is null)
            exit = StayBoundary.Estimated(entry!.At + candidate.Policy.EstimatedDuration);

        return new(entry, exit, candidate.Policy, candidate.Groups.Concat(Slices(associations)), associations);
    }

    private static IEnumerable<ObservationGroup> Slices(IEnumerable<UnknownObservationAssociation> associations) =>
        associations.GroupBy(a => a.OriginalGroup).Select(g =>
            new ObservationGroup(g.Select(a => a.Observation).Order(StayObservationOrder.Instance).ToList()));
}
