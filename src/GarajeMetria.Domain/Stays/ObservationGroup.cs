using GarajeMetria.Domain.Lpr;

namespace GarajeMetria.Domain.Stays;

public sealed class ObservationGroup
{
    public static ObservationGroup Restore(IEnumerable<StayObservation> observations)
    {
        var members = observations.Order(StayObservationOrder.Instance).ToList();
        if (members.Count == 0 || members.Select(x => x.EventId).Distinct().Count() != members.Count ||
            members.Any(x => !x.IsPlateValid || x.Direction != members[0].Direction || x.WorkshopId != members[0].WorkshopId ||
                x.PlateNormalized != members[0].PlateNormalized)) throw new ArgumentException("Invalid saved observation group.");
        return new(members);
    }
    internal ObservationGroup(List<StayObservation> members)
    {
        Members = Array.AsReadOnly(members.ToArray());
    }

    public IReadOnlyList<StayObservation> Members { get; }
    public StayObservation EntryRepresentative => Members[0];
    public StayObservation ExitRepresentative => Members[^1];
    public DateTimeOffset FirstCapturedAt => EntryRepresentative.CapturedAt;
    public DateTimeOffset LastCapturedAt => ExitRepresentative.CapturedAt;
    public LprDirection? Direction => EntryRepresentative.Direction;
    public override string ToString() => nameof(ObservationGroup);
}
