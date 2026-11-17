using GarajeMetria.Domain.Lpr;

namespace GarajeMetria.Domain.Stays;

public static class StayObservationGrouping
{
    // The caller supplies the historical policy for an existing stay's context.
    // Page boundaries must not be treated as complete calculation contexts.
    public static IReadOnlyList<ObservationGroup> Group(IEnumerable<StayObservation> observations,
        string workshopId, string plateNormalized, StayPolicySnapshot policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return GroupWithPolicies(observations, workshopId, plateNormalized, _ => policy);
    }

    public static IReadOnlyList<ObservationGroup> GroupWithPolicies(IEnumerable<StayObservation> observations,
        string workshopId, string plateNormalized, Func<StayObservation, StayPolicySnapshot> policyForFirst)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(policyForFirst);
        if (!LprEventRules.ValidText(workshopId, 256) || !LprEventRules.ValidText(plateNormalized, 32))
            throw new ArgumentException("Invalid observation partition.");

        var ordered = observations.Where(o => o.IsPlateValid &&
                string.Equals(o.WorkshopId, workshopId, StringComparison.Ordinal) &&
                string.Equals(o.PlateNormalized, plateNormalized, StringComparison.Ordinal))
            .Order(StayObservationOrder.Instance);
        var groups = new List<List<StayObservation>>();
        List<StayObservation>? entries = null, exits = null, unknown = null;

        foreach (var observation in ordered)
        {
            if (observation.Direction == LprDirection.Entry)
            {
                exits = null;
                Add(ref entries, observation);
            }
            else if (observation.Direction == LprDirection.Exit)
            {
                entries = null;
                Add(ref exits, observation);
            }
            else
            {
                Add(ref unknown, observation);
            }
        }

        return Array.AsReadOnly(groups.Select(members => new ObservationGroup(members)).ToArray());

        void Add(ref List<StayObservation>? group, StayObservation observation)
        {
            if (group is null || observation.CapturedAt - group[0].CapturedAt > policyForFirst(group[0]).RepeatWindow)
            {
                group = [];
                groups.Add(group);
            }
            group.Add(observation);
        }
    }
}
