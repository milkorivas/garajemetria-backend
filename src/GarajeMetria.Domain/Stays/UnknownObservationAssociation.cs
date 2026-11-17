namespace GarajeMetria.Domain.Stays;

public sealed class UnknownObservationAssociation
{
    public static UnknownObservationAssociation Restore(StayObservation observation, ObservationGroup group, StayObservation? movement)
    {
        if (observation.Direction is not null || !group.Members.Contains(observation) ||
            movement is not null && (movement.Direction is null || !movement.IsPlateValid || movement.WorkshopId != observation.WorkshopId ||
                movement.PlateNormalized != observation.PlateNormalized)) throw new ArgumentException("Invalid saved unknown association.");
        return new(observation, group, movement);
    }
    internal UnknownObservationAssociation(StayObservation observation, ObservationGroup originalGroup,
        StayObservation? matchedMovement)
    {
        Observation = observation;
        OriginalGroup = originalGroup;
        MatchedMovement = matchedMovement;
    }

    public StayObservation Observation { get; }
    public ObservationGroup OriginalGroup { get; }
    // Null means daily fallback, never a fabricated explicit direction.
    public StayObservation? MatchedMovement { get; }
    public override string ToString() => nameof(UnknownObservationAssociation);
}
