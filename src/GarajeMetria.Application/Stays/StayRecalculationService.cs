using System.Diagnostics;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Domain.Lpr;
using GarajeMetria.Domain.Stays;

namespace GarajeMetria.Application.Stays;

public sealed class StayRecalculationService(IStayRepository stays, IStayObservationQuery observations, IStayPolicyRepository policies,
    IStayClosureRepository closures, IWorkshopCalendar calendar, StayProcessingLimits limits, IStayProcessingObserver observer)
    : IStayRecalculationService
{
    public async Task<StayRecalculationResult> RecalculateAsync(StayPartition partition, DateTimeOffset seed, DateTimeOffset now,
        StayProjectionCause cause, StayObservation? pendingObservation, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var active = (await policies.GetActiveAsync(ct)).Policy;
            var window = await policies.GetMaximumContextWindowAsync(partition.Id, ct);
            var seedDate = calendar.GetLocalDate(seed);
            var pendingDates = new Queue<DateOnly>();
            var visitedDates = new HashSet<DateOnly>();
            var readings = new Dictionary<Guid, StayObservation>();
            var previous = new Dictionary<Guid, StayProjection>();
            StayRecalculation changes;
            Enqueue(seedDate);
            while (true)
            {
                while (pendingDates.TryDequeue(out var date))
                {
                    ct.ThrowIfCancellationRequested();
                    var day = calendar.GetDay(date);
                    var start = day.Start.Subtract(window); var end = day.End.Add(window);
                    StayObservationCursor? cursor = null;
                    do
                    {
                        var page = await observations.ReadAsync(new(partition, start, end, limits.PageSize), cursor, ct);
                        foreach (var item in page.Items) readings[item.Observation.EventId] = item.Observation;
                        cursor = page.NextCursor;
                        Budget(readings.Count, previous.Count);
                    } while (cursor is not null);
                    long? after = null;
                    do
                    {
                        var page = await stays.ReadContextAsync(new(partition, start, end, limits.PageSize), after, ct);
                        foreach (var old in page.Items)
                        {
                            previous[old.Id] = old;
                            foreach (var o in Evidence(old.Candidate)) readings[o.EventId] = o;
                        }
                        after = page.NextCreationOrder;
                        Budget(readings.Count, previous.Count);
                    } while (after is not null);
                }
                if (pendingObservation is { } supplied)
                {
                    if (supplied.WorkshopId != partition.WorkshopId || supplied.PlateNormalized != partition.PlateNormalized) throw Unavailable();
                    if (readings.TryGetValue(supplied.EventId, out var saved) && saved != supplied) throw Unavailable();
                    readings[supplied.EventId] = supplied;
                }
                var valid = readings.Values.Where(o => o.IsPlateValid).Order(StayObservationOrder.Instance).ToArray();
                Budget(readings.Count, previous.Count);
                if (valid.Length == 0)
                {
                    if (cause == StayProjectionCause.DailyClosure && (partition.NextClosureAt is null || partition.NextClosureAt > now))
                        return new(partition.InputRevision, partition.ProjectionRevision, false);
                    throw Unavailable();
                }
                changes = Interpret(partition, valid, previous.Values.ToArray(), active, now, ct);
                var owned = previous.Values.SelectMany(p => p.Candidate.Observations).Select(o => o.EventId).ToHashSet();
                changes = changes.KeepRelevant(p => calendar.GetLocalDate(p.Candidate.Observations[0].CapturedAt) == seedDate ||
                    p.Candidate.Observations.Any(o => o.EventId == pendingObservation?.EventId || owned.Contains(o.EventId)));
                var affected = changes.Created.Concat(changes.Updated).ToArray();
                foreach (var projection in affected)
                    foreach (var reading in Evidence(projection.Candidate)) Enqueue(calendar.GetLocalDate(reading.CapturedAt));
                // Read only adjacent explicit movements beyond M. An Entry is a stable separator;
                // another Exit can extend an already observed visit without a general duration cap.
                var movements = affected.SelectMany(p => p.Candidate.Observations).Where(o => o.Direction is not null)
                    .Order(StayObservationOrder.Instance).ToArray();
                if (movements.FirstOrDefault()?.Direction == LprDirection.Exit)
                {
                    var neighbour = await observations.ReadAdjacentExplicitAsync(partition, movements[0].CapturedAt, StayReadDirection.Backward, ct);
                    if (neighbour?.Direction == LprDirection.Exit) Enqueue(calendar.GetLocalDate(neighbour.CapturedAt));
                }
                if (movements.LastOrDefault()?.Direction == LprDirection.Exit)
                {
                    var neighbour = await observations.ReadAdjacentExplicitAsync(partition, movements[^1].CapturedAt, StayReadDirection.Forward, ct);
                    if (neighbour?.Direction == LprDirection.Exit) Enqueue(calendar.GetLocalDate(neighbour.CapturedAt));
                }
                if (pendingDates.Count == 0) break;
            }
            ct.ThrowIfCancellationRequested();
            var relevantDays = changes.Active.SelectMany(p => p.Candidate.Observations).Select(o => calendar.GetLocalDate(o.CapturedAt))
                .Where(visitedDates.Contains).Distinct().Order().ToArray();
            var deadlines = changes.Active.ToDictionary(p => p.Id, p =>
            {
                var root = p.Candidate.Groups.FirstOrDefault(g => g.Direction is not null) ?? p.Candidate.Groups[0];
                var endOfDay = calendar.GetDay(calendar.GetLocalDate(root.FirstCapturedAt)).End;
                return endOfDay > now ? (DateTimeOffset?)endOfDay : null;
            });
            var request = new StayProjectionWrite(partition.Id, partition.InputRevision, partition.ProjectionRevision, cause, now,
                cause == StayProjectionCause.Ingestion ? pendingObservation?.EventId ?? throw Unavailable() : null, deadlines.Values.Min(), changes)
            { NextClosures = deadlines };
            var write = await stays.ApplyAsync(request, ct);
            foreach (var date in relevantDays.Where(date => calendar.GetDay(date).End <= now))
                await closures.RecordAsync(new(partition.Id, date, partition.InputRevision, now, null), ct);
            observer.Recalculated(cause, readings.Count, changes.Created.Count, changes.Updated.Count, changes.Absorbed.Count,
                changes.Active.Sum(p => (p.Candidate.Entry?.Source == StayBoundarySource.Estimated ? 1 : 0) +
                    (p.Candidate.Exit?.Source == StayBoundarySource.Estimated ? 1 : 0)), Stopwatch.GetElapsedTime(started));
            return new(partition.InputRevision, write.ProjectionRevision, changes.HasChanges);

            void Enqueue(DateOnly date)
            {
                if (visitedDates.Add(date)) pendingDates.Enqueue(date);
            }
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException or NotSupportedException)
        { throw new PersistenceFailureException(FailureCode.ServiceUnavailable, ex); }
    }

    private StayRecalculation Interpret(StayPartition partition, StayObservation[] valid, IReadOnlyCollection<StayProjection> previous,
        StayPolicySnapshot active, DateTimeOffset now, CancellationToken ct)
    {
        var days = Days(valid);
        var historical = previous.Select(p => p.Candidate.Policy).Append(active).ToArray();
        // A conservative seed can expose a potential fusion under any historical R/M. The reconciler
        // reevaluates with the complete winner snapshot before any identity is persisted.
        var seedPolicy = new StayPolicySnapshot(Guid.NewGuid(), historical.MaxBy(p => p.EstimatedDuration)!.EstimatedDuration,
            historical.MaxBy(p => p.RepeatWindow)!.RepeatWindow, historical.MaxBy(p => p.MaximumAssociationDuration)!.MaximumAssociationDuration,
            active.TimeZoneId, 1);
        var candidates = Evaluate(valid, seedPolicy);
        var identities = new Dictionary<Guid, Guid>();
        StayRecalculation changes;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var order = partition.LastCreationOrder;
            changes = StayRecalculator.Recalculate(previous, candidates, now, Evaluate, o =>
            {
                if (!identities.TryGetValue(o.EventId, out var id)) identities[o.EventId] = id = Guid.NewGuid();
                return new(id, checked(++order));
            }, active);
            // Resolve unknown proximity globally again after a winner changes X/R/M. Reevaluation of a
            // component alone must not hide an eligible explicit movement in the neighbouring component.
            var targets = changes.Active.SelectMany(p => p.Candidate.Observations.Select(o => (o.EventId, p.Candidate.Policy)))
                .ToDictionary(x => x.EventId, x => x.Policy);
            var explicitCandidates = changes.Active.SelectMany(p =>
            {
                var groups = StayObservationGrouping.Group(p.Candidate.Observations.Where(o => o.Direction is not null),
                    partition.WorkshopId, partition.PlateNormalized, p.Candidate.Policy);
                return ExplicitStayCalculator.Calculate(groups, days, now, _ => p.Candidate.Policy);
            }).ToArray();
            var unknownGroups = StayObservationGrouping.GroupWithPolicies(valid.Where(o => o.Direction is null), partition.WorkshopId,
                partition.PlateNormalized, GroupPolicy);
            var next = UnknownStayCalculator.Calculate(explicitCandidates, unknownGroups, days, now,
                o => targets.GetValueOrDefault(o.EventId) ?? active);
            var current = changes.Active.Select(p => p.Candidate).OrderBy(c => c.Observations[0], StayObservationOrder.Instance).ToArray();
            var sorted = next.OrderBy(c => c.Observations[0], StayObservationOrder.Instance).ToArray();
            if (current.Length == sorted.Length && current.Zip(sorted).All(pair => StayRecalculator.SameCandidate(pair.First, pair.Second))) break;
            candidates = next;

            StayPolicySnapshot GroupPolicy(StayObservation first)
            {
                var proposed = targets.GetValueOrDefault(first.EventId) ?? active;
                // A late first member must not use a newer/larger R to absorb an older historical
                // group across midnight. The first-created historical owner decides that boundary.
                var owner = previous.Where(p => p.Candidate.Observations.Any(o => o.Direction is null &&
                    o.CapturedAt >= first.CapturedAt && o.CapturedAt - first.CapturedAt <=
                    (p.Candidate.Policy.RepeatWindow > proposed.RepeatWindow ? p.Candidate.Policy.RepeatWindow : proposed.RepeatWindow)))
                    .OrderBy(p => p.CreatedAt).ThenBy(p => p.CreationOrder).FirstOrDefault();
                return owner?.Candidate.Policy ?? proposed;
            }
        }
        return changes;
        IReadOnlyList<StayCandidate> Evaluate(IReadOnlyList<StayObservation> values, StayPolicySnapshot policy) =>
            StayCalculator.Calculate(values, policy, days, now);
    }

    private static IEnumerable<StayObservation> Evidence(StayCandidate candidate) => candidate.Observations
        .Concat(candidate.UnknownAssociations.Select(a => a.OriginalGroup).Distinct().SelectMany(g => g.Members));

    private IReadOnlyList<StayDayWindow> Days(IEnumerable<StayObservation> readings) =>
        readings.Select(o => calendar.GetLocalDate(o.CapturedAt)).Distinct().Order().Select(calendar.GetDay).ToArray();

    private void Budget(int readings, int projections)
    { if (readings > limits.ObservationLimit || projections > limits.StayLimit) throw Unavailable(); }
    private static PersistenceFailureException Unavailable() => new(FailureCode.ServiceUnavailable);
}
