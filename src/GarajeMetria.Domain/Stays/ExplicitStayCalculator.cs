using GarajeMetria.Domain.Lpr;

namespace GarajeMetria.Domain.Stays;

public static class ExplicitStayCalculator
{
    // Groups must already use the appropriate historical R. The policy resolver is
    // a pure lookup in supplied context, never a repository or clock operation.
    public static IReadOnlyList<ExplicitStayCandidate> Calculate(IEnumerable<ObservationGroup> groups,
        IReadOnlyList<StayDayWindow> days, DateTimeOffset now,
        Func<StayObservation, StayPolicySnapshot> policyForOrigin)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(policyForOrigin);
        var calendar = days.OrderBy(d => d.Start).ToArray();
        if (calendar.Select(d => d.Date).Distinct().Count() != calendar.Length ||
            calendar.Zip(calendar.Skip(1)).Any(pair => pair.First.End > pair.Second.Start))
            throw new ArgumentException("Stay day windows must have unique dates and cannot overlap.");

        var explicitGroups = groups.Where(g => g.Direction is not null)
            .OrderBy(g => g.EntryRepresentative, StayObservationOrder.Instance).ToArray();
        var origin = explicitGroups.FirstOrDefault()?.EntryRepresentative;
        if (origin is null) return Array.Empty<ExplicitStayCandidate>();
        if (explicitGroups.SelectMany(g => g.Members).Any(o =>
            !string.Equals(o.WorkshopId, origin.WorkshopId, StringComparison.Ordinal) ||
            !string.Equals(o.PlateNormalized, origin.PlateNormalized, StringComparison.Ordinal)))
            throw new ArgumentException("Explicit calculation requires one vehicle and workshop.");

        var candidates = new List<Builder>();
        Builder? current = null;
        foreach (var group in explicitGroups)
        {
            var day = DayAt(group.FirstCapturedAt);
            if (group.Direction == LprDirection.Entry)
            {
                if (current is null || current.EntryGroup is null || current.ExitGroup is not null || current.Day.Date != day.Date)
                    Start(group, day);
                else
                    current.Groups.Add(group);
            }
            else if (current is null || current.EntryGroup is not null && current.ExitGroup is null &&
                group.FirstCapturedAt >= current.Day.End &&
                group.LastCapturedAt - current.EntryGroup.FirstCapturedAt > current.Policy.MaximumAssociationDuration)
            {
                Start(group, day);
            }
            else
            {
                current.Groups.Add(group);
                current.ExitGroup = group;
            }
        }

        return Array.AsReadOnly(candidates.Select(candidate => candidate.Finish(now)).ToArray());

        StayDayWindow DayAt(DateTimeOffset at) => calendar.FirstOrDefault(d => d.Contains(at)) ??
            throw new ArgumentException("Calendar bounds are missing for an explicit group.");

        void Start(ObservationGroup group, StayDayWindow day)
        {
            var policy = policyForOrigin(group.EntryRepresentative) ??
                throw new ArgumentException("A candidate requires its historical or active policy.");
            current = new Builder(group, day, policy);
            candidates.Add(current);
        }
    }

    private sealed class Builder
    {
        public Builder(ObservationGroup group, StayDayWindow day, StayPolicySnapshot policy)
        {
            Groups = [group];
            Day = day;
            Policy = policy;
            if (group.Direction == LprDirection.Entry) EntryGroup = group;
            else ExitGroup = group;
        }

        public List<ObservationGroup> Groups { get; }
        public StayDayWindow Day { get; }
        public StayPolicySnapshot Policy { get; }
        public ObservationGroup? EntryGroup { get; }
        public ObservationGroup? ExitGroup { get; set; }

        public ExplicitStayCandidate Finish(DateTimeOffset now)
        {
            StayBoundary? entry = EntryGroup is null ? null :
                StayBoundary.Observed(EntryGroup.EntryRepresentative, StayBoundarySource.Explicit);
            StayBoundary? exit = ExitGroup is null ? null :
                StayBoundary.Observed(ExitGroup.ExitRepresentative, StayBoundarySource.Explicit);
            if (now >= Day.End)
            {
                if (entry is not null && exit is null)
                    exit = Groups.Count > 1
                        ? StayBoundary.Observed(Groups[^1].ExitRepresentative, StayBoundarySource.Inferred)
                        : StayBoundary.Estimated(entry.At + Policy.EstimatedDuration);
                else if (entry is null && exit is not null)
                    entry = Groups.Count > 1
                        ? StayBoundary.Observed(Groups[0].EntryRepresentative, StayBoundarySource.Inferred)
                        : StayBoundary.Estimated(exit.At - Policy.EstimatedDuration);
            }
            return new(entry, exit, Policy, Groups);
        }
    }
}
