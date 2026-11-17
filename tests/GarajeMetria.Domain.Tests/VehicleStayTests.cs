using GarajeMetria.Domain.Lpr;
using GarajeMetria.Domain.Stays;
using GarajeMetria.Testing;

namespace GarajeMetria.Domain.Tests;

public sealed class VehicleStayTests
{
    private static readonly Guid StayId = StayTestData.FusionWinner.Id;
    private static readonly Guid DeviceId = Guid.Parse("20000000-0000-4000-8000-000000000001");
    private static readonly DateTimeOffset At = StayTestData.Monday.AddHours(8);

    private static StayPolicySnapshot Policy(StayFixturePolicy? values = null)
    {
        values ??= StayTestData.OriginalPolicy;
        return new(StayId, TimeSpan.FromMinutes(values.EstimatedMinutes), TimeSpan.FromSeconds(values.RepeatSeconds),
            TimeSpan.FromHours(values.MaximumHours), "Atlantic/Canary", 1);
    }

    private static StayObservation Observation(int sequence, DateTimeOffset at, LprDirection? direction,
        string workshop = "synthetic-workshop", string plate = "1234BCD", bool valid = true) =>
        new(StayTestData.Reading(sequence, at, null).Id, DeviceId, workshop, plate, at, at.AddSeconds(1),
            valid, "saved-v1", 0.01m, direction);

    private static StayBoundary Explicit(StayObservation observation) =>
        StayBoundary.Observed(observation, StayBoundarySource.Explicit);

    private static VehicleStay Open(StayPolicySnapshot? policy = null)
    {
        var observation = Observation(1, At, LprDirection.Entry);
        return new(StayId, observation, At.AddMinutes(1), 1, policy ?? Policy(), Explicit(observation), null);
    }

    [Fact]
    public void AnOpenStayUsesTheSuppliedInstantWithoutPersistingATickingDuration()
    {
        var stay = Open();
        Assert.Equal(StayState.Open, stay.State);
        Assert.Null(stay.ExitAt);
        Assert.Null(stay.Duration);
        Assert.Equal(TimeSpan.FromMinutes(10), stay.GetDurationAt(At.AddMinutes(10)));
        Assert.Equal(TimeSpan.FromMinutes(25), stay.GetDurationAt(At.AddMinutes(25)));
        Assert.Equal(At.AddMinutes(1), stay.UpdatedAt);
        Assert.Null(stay.Duration);
    }

    [Fact]
    public void FutureCaptureMayFollowCreationButCannotExposeNegativeElapsedDuration()
    {
        var observation = Observation(1, At.AddMinutes(15), LprDirection.Entry);
        var stay = new VehicleStay(StayId, observation, At, 1, Policy(), Explicit(observation), null);
        Assert.Equal(At, stay.CreatedAt);
        Assert.Null(stay.GetDurationAt(At));
        Assert.Equal(TimeSpan.Zero, stay.GetDurationAt(observation.CapturedAt));
    }

    [Fact]
    public void PendingExitHasNoInventedEntryOrDurationUntilAnEstimateIsProvided()
    {
        var observation = Observation(1, At, LprDirection.Exit);
        var exit = Explicit(observation);
        var stay = new VehicleStay(StayId, observation, At.AddMinutes(1), 1, Policy(), null, exit);
        Assert.Equal(StayState.PendingEntry, stay.State);
        Assert.Null(stay.EntryAt);
        Assert.Null(stay.GetDurationAt(At.AddDays(1)));
        var estimated = StayBoundary.Estimated(At.AddMinutes(-30));
        Assert.True(stay.ReviseBoundaries(estimated, exit, At.AddHours(1)));
        Assert.Equal(StayState.ClosedProvisional, stay.State);
        Assert.Null(stay.Entry!.EventId);
        Assert.Equal(StayBoundarySource.Estimated, stay.Entry.Source);
        Assert.Equal(observation.EventId, stay.Exit!.EventId);
        Assert.Equal(TimeSpan.FromMinutes(30), stay.Duration);
    }

    [Fact]
    public void VirtualExitCanBeReplacedByAnObservedExitWithoutChangingIdentityOrPolicy()
    {
        var policy = Policy();
        var stay = Open(policy);
        var entry = stay.Entry;
        stay.ReviseBoundaries(entry, StayBoundary.Estimated(At.AddMinutes(30)), At.AddHours(16));
        Assert.Equal(StayState.ClosedProvisional, stay.State);
        Assert.Null(stay.Exit!.Observation);
        var realExit = Observation(2, At.AddDays(1), LprDirection.Exit);
        Assert.True(stay.ReviseBoundaries(entry, Explicit(realExit), At.AddDays(1).AddMinutes(1)));
        Assert.Equal(StayBoundarySource.Explicit, stay.Exit!.Source);
        Assert.Equal(realExit.EventId, stay.Exit.EventId);
        Assert.Equal(StayId, stay.Id);
        Assert.Equal(At.AddMinutes(1), stay.CreatedAt);
        Assert.Equal(1, stay.CreationOrder);
        Assert.Same(policy, stay.Policy);
        Assert.Equal(TimeSpan.FromDays(1), stay.Duration);
        Assert.Equal(stay.Duration, stay.GetDurationAt(At.AddDays(2)));
    }

    [Fact]
    public void EarlierCaptureChangesTheEntryWithoutReplacingTheOriginOrHistoricalConfiguration()
    {
        var policy = Policy();
        var stay = Open(policy);
        var origin = stay.OriginEventId;
        var older = Observation(2, At.AddHours(-1), LprDirection.Entry);
        var newConfiguration = Policy(StayTestData.ChangedPolicy);
        Assert.True(stay.ReviseBoundaries(Explicit(older), null, At.AddHours(1)));
        Assert.Equal(older.CapturedAt, stay.EntryAt);
        Assert.Equal(origin, stay.OriginEventId);
        Assert.NotEqual(older.EventId, stay.OriginEventId);
        Assert.Same(policy, stay.Policy);
        Assert.NotEqual(newConfiguration, stay.Policy);
        Assert.Equal(At.AddMinutes(1), stay.CreatedAt);
    }

    [Fact]
    public void EstimatesUseTheSuppliedSnapshotInsteadOfADefaultDuration()
    {
        var stay = Open(Policy(StayTestData.ChangedPolicy));
        Assert.True(stay.ReviseBoundaries(stay.Entry, StayBoundary.Estimated(At.AddMinutes(45)), At.AddHours(1)));
        Assert.Equal(TimeSpan.FromMinutes(45), stay.Duration);
        Assert.Throws<ArgumentException>(() => stay.ReviseBoundaries(stay.Entry,
            StayBoundary.Estimated(At.AddMinutes(30)), At.AddHours(2)));
        Assert.Equal(TimeSpan.FromMinutes(45), stay.Duration);
        Assert.Equal(At.AddHours(1), stay.UpdatedAt);
    }

    [Fact]
    public void DurationUsesInstantsAcrossMidnightAndBothDaylightSavingTransitions()
    {
        foreach (var (start, end, expected) in new[]
        {
            (StayTestData.Monday.AddHours(23).AddMinutes(50), StayTestData.Monday.AddDays(1).AddMinutes(20), TimeSpan.FromMinutes(30)),
            (new DateTimeOffset(2026, 3, 29, 0, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 29, 2, 30, 0, TimeSpan.FromHours(1)), TimeSpan.FromHours(1)),
            (new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.FromHours(1)), new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero), TimeSpan.FromHours(1))
        })
        {
            var origin = Observation(1, start, LprDirection.Entry);
            var exit = Observation(2, end, LprDirection.Exit);
            var stay = new VehicleStay(StayId, origin, start, 1, Policy(), Explicit(origin), Explicit(exit));
            Assert.Equal(expected, stay.Duration);
            Assert.Equal(TimeSpan.Zero, stay.EntryAt!.Value.Offset);
            Assert.Equal(TimeSpan.Zero, stay.ExitAt!.Value.Offset);
        }
    }

    [Fact]
    public void InferredBoundaryKeepsTheActualObservationAndItsOriginalDirection()
    {
        var entry = Observation(1, At, LprDirection.Entry);
        var laterEntry = Observation(2, At.AddHours(8), LprDirection.Entry);
        var inferredExit = StayBoundary.Observed(laterEntry, StayBoundarySource.Inferred);
        var stay = new VehicleStay(StayId, entry, At, 1, Policy(), Explicit(entry), inferredExit);
        Assert.Equal(laterEntry.CapturedAt, stay.ExitAt);
        Assert.Equal(laterEntry.EventId, stay.Exit!.EventId);
        Assert.Equal(LprDirection.Entry, stay.Exit.Observation!.Direction);
        Assert.Equal(TimeSpan.FromHours(8), stay.Duration);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BoundaryFromAnotherWorkshopOrPlateCannotModifyTheStay(bool differentWorkshop)
    {
        var stay = Open();
        var other = Observation(2, At.AddHours(1), LprDirection.Exit,
            differentWorkshop ? "other-workshop" : "synthetic-workshop", differentWorkshop ? "1234BCD" : "9999BCD");
        Assert.Throws<ArgumentException>(() => stay.ReviseBoundaries(stay.Entry, Explicit(other), At.AddHours(2)));
        Assert.Equal(StayState.Open, stay.State);
        Assert.Null(stay.Exit);
        Assert.Equal(At.AddMinutes(1), stay.UpdatedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(LprDirection.Entry)]
    public void AnExplicitExitCannotClaimAnUnknownOrEntryObservation(LprDirection? direction)
    {
        var stay = Open();
        var observation = Observation(2, At.AddHours(1), direction);
        Assert.Throws<ArgumentException>(() => stay.ReviseBoundaries(stay.Entry, Explicit(observation), At.AddHours(2)));
        Assert.Null(stay.Exit);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(LprDirection.Exit)]
    public void AnExplicitEntryCannotClaimAnUnknownOrExitObservation(LprDirection? direction)
    {
        var observation = Observation(1, At, direction);
        Assert.Throws<ArgumentException>(() => new VehicleStay(StayId, observation, At, 1, Policy(), Explicit(observation), null));
    }

    [Fact]
    public void InvalidRevisionsAreAtomicAndDoNotChangeThePreviousState()
    {
        var stay = Open();
        var original = stay.Entry;
        var earlyExit = Explicit(Observation(2, At.AddTicks(-1), LprDirection.Exit));
        Assert.Throws<ArgumentException>(() => stay.ReviseBoundaries(original, earlyExit, At.AddHours(1)));
        Assert.Throws<ArgumentException>(() => stay.ReviseBoundaries(null, null, At.AddHours(1)));
        Assert.Throws<ArgumentException>(() => stay.ReviseBoundaries(original, null, stay.CreatedAt.AddTicks(-1)));
        Assert.Same(original, stay.Entry);
        Assert.Null(stay.Exit);
        Assert.Equal(stay.CreatedAt, stay.UpdatedAt);
    }

    [Fact]
    public void UnchangedRevisionIsANoOpAndClosedStayCanReopenForRecalculation()
    {
        var stay = Open();
        Assert.False(stay.ReviseBoundaries(stay.Entry, null, At.AddHours(1)));
        Assert.Equal(stay.CreatedAt, stay.UpdatedAt);
        var exit = Explicit(Observation(2, At.AddHours(1), LprDirection.Exit));
        stay.ReviseBoundaries(stay.Entry, exit, At.AddHours(2));
        Assert.True(stay.ReviseBoundaries(stay.Entry, null, At.AddHours(3)));
        Assert.Equal(StayState.Open, stay.State);
        Assert.Null(stay.Duration);
    }

    [Fact]
    public void ZeroObservedDurationIsValidButUnsupportedOrInaccurateEstimatesAreNot()
    {
        var origin = Observation(1, At, LprDirection.Entry);
        var entry = Explicit(origin);
        var exit = Explicit(Observation(2, At, LprDirection.Exit));
        var stay = new VehicleStay(StayId, origin, At, 1, Policy(), entry, exit);
        Assert.Equal(TimeSpan.Zero, stay.Duration);
        Assert.Throws<ArgumentException>(() => new VehicleStay(StayId, origin, At, 1, Policy(), null, null));
        Assert.Throws<ArgumentException>(() => new VehicleStay(StayId, origin, At, 1, Policy(), StayBoundary.Estimated(At), null));
        Assert.Throws<ArgumentException>(() => new VehicleStay(StayId, origin, At, 1, Policy(), null, StayBoundary.Estimated(At)));
        Assert.Throws<ArgumentException>(() => new VehicleStay(StayId, origin, At, 1, Policy(),
            StayBoundary.Estimated(At), StayBoundary.Estimated(At.AddMinutes(30))));
        Assert.Throws<ArgumentException>(() => new VehicleStay(StayId, origin, At, 1, Policy(), entry, StayBoundary.Estimated(At.AddMinutes(45))));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CreationOrderMustBePositive(long order)
    {
        var origin = Observation(1, At, LprDirection.Entry);
        Assert.Throws<ArgumentException>(() => new VehicleStay(StayId, origin, At, order, Policy(), Explicit(origin), null));
    }

    [Fact]
    public void IdentityAndOriginAreRequiredAndSensitiveValuesDoNotAppearInStringRepresentations()
    {
        var origin = Observation(1, At, LprDirection.Entry);
        var entry = Explicit(origin);
        Assert.Throws<ArgumentException>(() => new VehicleStay(Guid.Empty, origin, At, 1, Policy(), entry, null));
        Assert.Throws<ArgumentNullException>(() => new VehicleStay(StayId, null!, At, 1, Policy(), entry, null));
        Assert.Throws<ArgumentNullException>(() => new VehicleStay(StayId, origin, At, 1, null!, entry, null));
        var invalid = Observation(2, At, null, valid: false);
        Assert.Throws<ArgumentException>(() => new VehicleStay(StayId, invalid, At, 1, Policy(), entry, null));
        Assert.Equal(nameof(StayObservation), origin.ToString());
        Assert.Equal(nameof(StayBoundary), entry.ToString());
        Assert.Equal(nameof(VehicleStay), Open().ToString());
    }
}
