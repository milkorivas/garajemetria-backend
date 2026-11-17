namespace GarajeMetria.Domain.Stays;

public static class StayCalculator
{
    public static IReadOnlyList<StayCandidate> Calculate(IEnumerable<StayObservation> observations,
        StayPolicySnapshot policy, IReadOnlyList<StayDayWindow> days, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.AlgorithmVersion != 1)
            throw new NotSupportedException("The historical stay algorithm version requires its own evaluator.");
        var saved = observations.ToArray();
        if (saved.Length == 0)
            return UnknownStayCalculator.Calculate([], [], days, now, _ => policy);
        if (saved.Any(o => o.WorkshopId != saved[0].WorkshopId || o.PlateNormalized != saved[0].PlateNormalized) ||
            saved.Select(o => o.EventId).Distinct().Count() != saved.Length)
            throw new ArgumentException("Calculation requires unique events from one partition.");
        var groups = StayObservationGrouping.Group(saved, saved[0].WorkshopId, saved[0].PlateNormalized, policy);
        var explicitCandidates = ExplicitStayCalculator.Calculate(groups, days, now, _ => policy);
        return UnknownStayCalculator.Calculate(explicitCandidates, groups.Where(g => g.Direction is null), days, now, _ => policy);
    }
}
