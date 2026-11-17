namespace GarajeMetria.Domain.Stays;

public static class StayRecalculator
{
    // Both callbacks are pure: evaluation dispatches by historical algorithm
    // version; allocations come from caller-supplied IDs and partition order.
    public static StayRecalculation Recalculate(IEnumerable<StayProjection> previous,
        IEnumerable<StayCandidate> candidates, DateTimeOffset now,
        Func<IReadOnlyList<StayObservation>, StayPolicySnapshot, IReadOnlyList<StayCandidate>> evaluate,
        Func<StayObservation, StayIdentityAllocation> allocate, StayPolicySnapshot? newPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(evaluate);
        ArgumentNullException.ThrowIfNull(allocate);
        var old = previous.OrderBy(p => p.CreatedAt).ThenBy(p => p.CreationOrder).ToArray();
        var current = candidates.OrderBy(c => c.Observations[0], StayObservationOrder.Instance).ToArray();
        ValidateContext(old, current, now);
        var owners = old.SelectMany(p => p.Candidate.Observations.Select(o => (o.EventId, Projection: p)))
            .ToDictionary(pair => pair.EventId, pair => pair.Projection);

        // Reevaluation may undo a provisional fusion (for example with historical
        // R instead of active R). Refine components before allocating or absorbing IDs.
        var components = Components(current, owners);
        while (components.Count > 0)
        {
            var recalculated = new List<StayCandidate>();
            foreach (var component in components)
            {
                var readings = component.SelectMany(c => c.Observations).Order(StayObservationOrder.Instance).ToArray();
                var winner = Winner(readings, owners);
                var policy = winner?.Candidate.Policy ?? newPolicy ?? component[0].Policy;
                // Already evaluated candidates retain their complete border/group
                // evidence. Reevaluate only when reconciliation changes the policy.
                var result = component.All(c => c.Policy == policy) ? component : evaluate(Array.AsReadOnly(readings), policy) ??
                    throw new ArgumentException("Evaluation must return complete candidates.");
                var returned = result.SelectMany(c => c.Observations).Order(StayObservationOrder.Instance).ToArray();
                if (!readings.SequenceEqual(returned) || result.Any(c => c.Policy != policy))
                    throw new ArgumentException("Evaluation must preserve all observations and the complete inherited policy.");
                recalculated.AddRange(result);
            }
            current = recalculated.OrderBy(c => c.Observations[0], StayObservationOrder.Instance).ToArray();
            var refined = Components(current, owners);
            if (refined.Count == components.Count)
            {
                components = refined;
                break;
            }
            if (refined.Count < components.Count)
                throw new InvalidOperationException("Evaluation cannot merge independent reconciliation contexts.");
            components = refined;
        }

        var active = new List<StayProjection>();
        var created = new List<StayProjection>();
        var updated = new List<StayProjection>();
        var absorbed = new List<StayProjection>();
        var lineage = new List<StayLineage>();
        var usedIds = old.Select(p => p.Id).ToHashSet();
        var usedOrders = old.Select(p => p.CreationOrder).ToHashSet();
        var lastOrder = old.Select(p => p.CreationOrder).DefaultIfEmpty().Max();
        foreach (var component in components)
        {
            var winner = Winner(component.SelectMany(c => c.Observations), owners);
            var sources = component.SelectMany(c => c.Observations).Where(o => owners.ContainsKey(o.EventId))
                .Select(o => owners[o.EventId]).Distinct().OrderBy(p => p.CreatedAt).ThenBy(p => p.CreationOrder);
            foreach (var source in sources.Where(p => p != winner))
            {
                absorbed.Add(source);
                lineage.Add(new(source.Id, winner!.Id, StayLineageKind.Fusion));
            }

            foreach (var candidate in component)
            {
                if (winner is not null && candidate.Observations.Any(o => o.EventId == winner.OriginEventId))
                {
                    if (SameCandidate(candidate, winner.Candidate))
                        active.Add(winner);
                    else
                    {
                        var revision = new StayProjection(winner.Id, winner.OriginEventId, winner.CreatedAt,
                            winner.CreationOrder, candidate, now);
                        active.Add(revision);
                        updated.Add(revision);
                    }
                }
                else
                {
                    var origin = candidate.Observations[0];
                    var identity = allocate(origin) ?? throw new ArgumentException("A new part requires identity allocation.");
                    if (!usedIds.Add(identity.Id) || !usedOrders.Add(identity.CreationOrder) || identity.CreationOrder <= lastOrder)
                        throw new ArgumentException("New stay identities and creation orders must be unique and increase.");
                    lastOrder = identity.CreationOrder;
                    var projection = new StayProjection(identity.Id, origin.EventId, now, identity.CreationOrder, candidate);
                    active.Add(projection);
                    created.Add(projection);
                    if (winner is not null) lineage.Add(new(winner.Id, projection.Id, StayLineageKind.Split));
                }
            }
        }

        return new(active.OrderBy(p => p.Candidate.Observations[0], StayObservationOrder.Instance),
            created, updated, absorbed, lineage);
    }

    private static void ValidateContext(StayProjection[] previous, StayCandidate[] candidates, DateTimeOffset now)
    {
        if (previous.Select(p => p.Id).Distinct().Count() != previous.Length ||
            previous.Select(p => p.CreationOrder).Distinct().Count() != previous.Length || previous.Any(p => p.UpdatedAt > now))
            throw new ArgumentException("Invalid previous projection identities, creation order or evaluation time.");
        var saved = previous.SelectMany(p => p.Candidate.Observations).ToArray();
        var readings = candidates.SelectMany(c => c.Observations).ToArray();
        if (saved.Select(o => o.EventId).Distinct().Count() != saved.Length ||
            readings.Select(o => o.EventId).Distinct().Count() != readings.Length)
            throw new ArgumentException("An observation cannot belong to several active candidates.");
        var current = readings.ToDictionary(o => o.EventId);
        if (saved.Any(o => !current.TryGetValue(o.EventId, out var value) || value != o))
            throw new ArgumentException("Recalculation requires complete, unchanged historical observations.");
        var origin = readings.FirstOrDefault();
        if (origin is not null && readings.Any(o => o.WorkshopId != origin.WorkshopId || o.PlateNormalized != origin.PlateNormalized))
            throw new ArgumentException("Recalculation requires a single vehicle and workshop.");
    }

    private static StayProjection? Winner(IEnumerable<StayObservation> observations, Dictionary<Guid, StayProjection> owners) =>
        observations.Where(o => owners.ContainsKey(o.EventId)).Select(o => owners[o.EventId]).Distinct()
            .OrderBy(p => p.CreatedAt).ThenBy(p => p.CreationOrder).FirstOrDefault();

    private static List<StayCandidate[]> Components(StayCandidate[] candidates, Dictionary<Guid, StayProjection> owners)
    {
        var parent = Enumerable.Range(0, candidates.Length).ToArray();
        var firstPart = new Dictionary<Guid, int>();
        for (var index = 0; index < candidates.Length; index++)
        {
            foreach (var observation in candidates[index].Observations)
            {
                if (!owners.TryGetValue(observation.EventId, out var owner)) continue;
                if (firstPart.TryGetValue(owner.Id, out var first)) parent[Root(index)] = Root(first);
                else firstPart.Add(owner.Id, index);
            }
        }
        return candidates.Select((candidate, index) => (Candidate: candidate, Root: Root(index)))
            .GroupBy(p => p.Root).Select(g => g.Select(p => p.Candidate).ToArray())
            .OrderBy(parts => parts[0].Observations[0], StayObservationOrder.Instance).ToList();

        int Root(int index)
        {
            while (parent[index] != index)
            {
                parent[index] = parent[parent[index]];
                index = parent[index];
            }
            return index;
        }
    }

    public static bool SameCandidate(StayCandidate left, StayCandidate right) =>
        left.Entry == right.Entry && left.Exit == right.Exit && left.Policy == right.Policy &&
        left.Observations.SequenceEqual(right.Observations) &&
        SameGroups(left.Groups, right.Groups) && left.UnknownAssociations.Count == right.UnknownAssociations.Count &&
        left.UnknownAssociations.Zip(right.UnknownAssociations).All(pair =>
            pair.First.Observation == pair.Second.Observation && pair.First.MatchedMovement == pair.Second.MatchedMovement &&
            pair.First.OriginalGroup.Members.SequenceEqual(pair.Second.OriginalGroup.Members));

    private static bool SameGroups(IReadOnlyList<ObservationGroup> left, IReadOnlyList<ObservationGroup> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => pair.First.Members.SequenceEqual(pair.Second.Members));
}
