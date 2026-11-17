using GarajeMetria.Domain.Lpr;
using GarajeMetria.Domain.Stays;
using GarajeMetria.Testing;

namespace GarajeMetria.Domain.Tests;

public sealed class UnknownStayCalculatorTests
{
    private static readonly DateTimeOffset Monday = StayTestData.Monday;
    private static readonly StayTestCalendar Calendar = new();
    private static readonly StayPolicySnapshot Policy = MakePolicy();

    [Fact]
    public void DailyFallbackClosesFromFirstToLastAndExpandsWithoutInventingIntermediateVisits()
    {
        var readings = new[] { Reading(1, Monday.AddHours(8)), Reading(2, Monday.AddHours(9)),
            Reading(3, Monday.AddHours(16)), Reading(4, Monday.AddHours(17)) };
        var afterTwo = Assert.Single(Calculate(readings.Take(2), Monday.AddHours(10)));
        Assert.Equal(StayState.ClosedProvisional, afterTwo.State);
        Assert.Equal(TimeSpan.FromHours(1), afterTwo.Duration);
        var afterAll = Assert.Single(Calculate(readings.Reverse(), Monday.AddHours(18)));
        AssertBoundary(afterAll.Entry, readings[0], StayBoundarySource.Inferred);
        AssertBoundary(afterAll.Exit, readings[3], StayBoundarySource.Inferred);
        Assert.Equal(TimeSpan.FromHours(9), afterAll.Duration);
        Assert.Equal(readings, afterAll.Observations);
        Assert.All(afterAll.UnknownAssociations, a => Assert.Null(a.MatchedMovement));
        Assert.All(readings, r => Assert.Null(r.Direction));
        Assert.Equal(readings[1].CapturedAt, afterTwo.Exit!.At);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void OneRepeatGroupStaysOpenUntilClosureInsteadOfCreatingAShortArtificialVisit(long ticks, bool distinct)
    {
        var first = Reading(1, Monday.AddHours(8));
        var last = Reading(2, first.CapturedAt.AddMinutes(1).AddTicks(ticks));
        var open = Assert.Single(Calculate([last, first], Monday.AddHours(10)));
        Assert.Equal(distinct ? StayState.ClosedProvisional : StayState.Open, open.State);
        if (distinct) AssertBoundary(open.Exit, last, StayBoundarySource.Inferred);
        else Assert.Null(open.Exit);
        var closed = Assert.Single(Calculate([first, last], Monday.AddDays(1)));
        Assert.Equal(distinct ? StayBoundarySource.Inferred : StayBoundarySource.Estimated, closed.Exit!.Source);
        Assert.Equal(distinct ? last.CapturedAt : first.CapturedAt.AddMinutes(30), closed.Exit.At);
        Assert.Equal(new[] { first, last }, closed.Observations);
    }

    [Fact]
    public void DailyFallbackSeparatesDifferentDaysButDoesNotCutARepeatGroupAtMidnight()
    {
        var first = Reading(1, Monday.AddHours(23).AddMinutes(59).AddSeconds(40));
        var repeat = Reading(2, first.CapturedAt.AddSeconds(30));
        var next = Reading(3, Monday.AddDays(1).AddHours(8));
        var last = Reading(4, Monday.AddDays(1).AddHours(9));
        var stays = Calculate([last, repeat, next, first], last.CapturedAt);
        Assert.Equal(2, stays.Count);
        Assert.Equal(new[] { first, repeat }, stays[0].Observations);
        Assert.Equal(first.CapturedAt.AddMinutes(30), stays[0].Exit!.At);
        Assert.Equal(StayBoundarySource.Estimated, stays[0].Exit!.Source);
        AssertBoundary(stays[1].Entry, next, StayBoundarySource.Inferred);
        AssertBoundary(stays[1].Exit, last, StayBoundarySource.Inferred);
    }

    [Fact]
    public void UnknownObservationsExpandBothExplicitBoundsAndPreserveTheirOriginalDirections()
    {
        var first = Reading(1, Monday.AddHours(7).AddMinutes(55));
        var entry = Reading(2, Monday.AddHours(8), LprDirection.Entry);
        var exit = Reading(3, Monday.AddHours(9), LprDirection.Exit);
        var last = Reading(4, Monday.AddHours(9).AddMinutes(5));
        var stay = Assert.Single(Calculate([last, exit, entry, first], Monday.AddHours(10)));
        AssertBoundary(stay.Entry, first, StayBoundarySource.Inferred);
        AssertBoundary(stay.Exit, last, StayBoundarySource.Inferred);
        Assert.Equal(TimeSpan.FromMinutes(70), stay.Duration);
        Assert.Same(entry, stay.UnknownAssociations[0].MatchedMovement);
        Assert.Same(exit, stay.UnknownAssociations[1].MatchedMovement);
        Assert.Null(first.Direction);
        Assert.Null(last.Direction);
        Assert.Equal(LprDirection.Entry, entry.Direction);
        Assert.Equal(LprDirection.Exit, exit.Direction);
        Assert.Equal(new[] { first, entry, exit, last }, stay.Observations);
    }

    [Fact]
    public void UnknownObservationInsideAnObservedVisitKeepsBothExplicitSupports()
    {
        var entry = Reading(1, Monday.AddHours(8), LprDirection.Entry);
        var unknown = Reading(2, Monday.AddHours(8).AddMinutes(20));
        var exit = Reading(3, Monday.AddHours(9), LprDirection.Exit);
        var stay = Assert.Single(Calculate([exit, unknown, entry], Monday.AddHours(10)));
        AssertBoundary(stay.Entry, entry, StayBoundarySource.Explicit);
        AssertBoundary(stay.Exit, exit, StayBoundarySource.Explicit);
        Assert.Same(entry, Assert.Single(stay.UnknownAssociations).MatchedMovement);
        Assert.Contains(unknown, stay.Observations);
    }

    [Fact]
    public void MembersOfOneUnknownGroupChooseDifferentVisitsAcrossTheProximityMidpoint()
    {
        var entry = Reading(1, Monday.AddHours(8), LprDirection.Entry);
        var exit = Reading(2, Monday.AddHours(9), LprDirection.Exit);
        var nextEntry = Reading(3, Monday.AddHours(16), LprDirection.Entry);
        var nextExit = Reading(4, Monday.AddHours(17), LprDirection.Exit);
        var before = Reading(5, Monday.AddHours(12).AddMinutes(30).AddTicks(-1));
        var tie = Reading(6, before.CapturedAt.AddTicks(1));
        var after = Reading(7, tie.CapturedAt.AddTicks(1));
        var readings = new[] { entry, exit, nextEntry, nextExit, before, tie, after };
        var stays = Calculate(readings.Reverse(), Monday.AddHours(18));
        Assert.Equal(2, stays.Count);
        Assert.Equal(new[] { before, tie }, stays[0].UnknownAssociations.Select(a => a.Observation));
        Assert.All(stays[0].UnknownAssociations, a => Assert.Same(exit, a.MatchedMovement));
        Assert.Same(nextEntry, Assert.Single(stays[1].UnknownAssociations).MatchedMovement);
        Assert.Same(after, stays[1].Entry!.Observation);
        var original = stays[0].UnknownAssociations[0].OriginalGroup;
        Assert.Equal(new[] { before, tie, after }, original.Members);
        Assert.Same(original, stays[1].UnknownAssociations[0].OriginalGroup);
        Assert.DoesNotContain(after, stays[0].Observations);
        Assert.DoesNotContain(before, stays[1].Observations);
        Assert.Equal(7, stays.Sum(s => s.Observations.Count));
        var forward = Calculate(readings, Monday.AddHours(18));
        Assert.Equal(stays.Select(s => (s.Entry, s.Exit)), forward.Select(s => (s.Entry, s.Exit)));
    }

    [Fact]
    public void AssociationToThePreviousDayTakesPriorityOverCreatingADailyFallback()
    {
        var entry = Reading(1, Monday.AddHours(23).AddMinutes(50), LprDirection.Entry);
        var unknown = Reading(2, Monday.AddDays(1).AddMinutes(5));
        var stay = Assert.Single(Calculate([unknown, entry], unknown.CapturedAt));
        AssertBoundary(stay.Entry, entry, StayBoundarySource.Explicit);
        AssertBoundary(stay.Exit, unknown, StayBoundarySource.Inferred);
        Assert.Same(entry, Assert.Single(stay.UnknownAssociations).MatchedMovement);
    }

    public static IEnumerable<object[]> AssociationRegressionCases => StayTestData.Cases
        .Where(c => c.AcceptanceCriterion == "CA-025").Select(c => new object[] { c.Id });

    [Theory]
    [MemberData(nameof(AssociationRegressionCases))]
    public void PreparedAssociationRegressionsNowProduceTheExpectedVisits(string id)
    {
        var fixture = StayTestData.Cases.Single(c => c.Id == id);
        var readings = fixture.Readings.Select((r, index) => Reading(index + 1, r.CapturedAt,
            r.Direction == "entry" ? LprDirection.Entry : r.Direction == "exit" ? LprDirection.Exit : null)).ToArray();
        var stays = Calculate(readings.Reverse(), readings.Max(r => r.CapturedAt));
        Assert.Equal(fixture.ExpectedStayCount, stays.Count);
        Assert.Equal(readings.Length, stays.Sum(s => s.Observations.Count));
        if (id == "equidistant-across-midnight")
        {
            Assert.Same(readings[0], Assert.Single(stays[0].UnknownAssociations).MatchedMovement);
            Assert.Empty(stays[1].UnknownAssociations);
        }
    }

    [Fact]
    public void EqualCaptureMovementTiesUseCanonicalUuidOrderEvenWhenCandidatesAreReversed()
    {
        var exit = Reading(1, Monday.AddHours(8), LprDirection.Exit);
        var entry = Reading(2, exit.CapturedAt, LprDirection.Entry);
        var unknown = Reading(3, exit.CapturedAt);
        var groups = StayObservationGrouping.Group([unknown, entry, exit], "synthetic-site", "1234BCD", Policy);
        var days = Days(exit.CapturedAt, exit.CapturedAt);
        var bases = ExplicitStayCalculator.Calculate(groups, days, exit.CapturedAt, _ => Policy);
        Assert.Equal(2, bases.Count);
        var stays = UnknownStayCalculator.Calculate(bases.Reverse(), groups.Where(g => g.Direction is null),
            days, exit.CapturedAt, _ => Policy);
        Assert.Same(exit, Assert.Single(stays.SelectMany(s => s.UnknownAssociations)).MatchedMovement);
        Assert.Null(stays[0].Entry);
        Assert.Equal(StayBoundarySource.Explicit, stays[0].Exit!.Source);
        Assert.Null(bases[0].Entry);
        Assert.Equal(exit.CapturedAt, bases[0].Exit!.At);
    }

    [Fact]
    public void DailyFallbackUsesLocalSummerDatesRatherThanUtcDates()
    {
        var first = Reading(1, new DateTimeOffset(2026, 6, 1, 22, 30, 0, TimeSpan.Zero));
        var next = Reading(2, first.CapturedAt.AddHours(1));
        var stays = Calculate([first, next], next.CapturedAt);
        Assert.Equal(2, stays.Count);
        Assert.Equal(StayBoundarySource.Estimated, stays[0].Exit!.Source);
        Assert.Equal(first.CapturedAt.AddMinutes(30), stays[0].Exit!.At);
        Assert.Equal(StayState.Open, stays[1].State);
        Assert.Same(next, stays[1].Entry!.Observation);
    }

    [Theory]
    [InlineData(3, 28)]
    [InlineData(10, 24)]
    public void AssociationMaximumUsesElapsedUtcAcrossBothSeasonalClockChanges(int month, int day)
    {
        var entry = Reading(1, new DateTimeOffset(2026, month, day, 8, 0, 0, TimeSpan.Zero), LprDirection.Entry);
        var unknown = Reading(2, entry.CapturedAt.AddHours(48));
        var stay = Assert.Single(Calculate([entry, unknown], unknown.CapturedAt));
        Assert.Equal(TimeSpan.FromHours(48), stay.Duration);
        Assert.Same(entry, Assert.Single(stay.UnknownAssociations).MatchedMovement);
        var outside = Reading(3, unknown.CapturedAt.AddTicks(1));
        Assert.Equal(2, Calculate([entry, outside], outside.CapturedAt).Count);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    public void UnknownAssociationHasAnInclusiveElapsedMaximumFromEntry(long ticks, int expected)
    {
        var entry = Reading(1, Monday.AddHours(8), LprDirection.Entry);
        var unknown = Reading(2, entry.CapturedAt.AddHours(48).AddTicks(ticks));
        var stays = Calculate([unknown, entry], unknown.CapturedAt);
        Assert.Equal(expected, stays.Count);
        if (expected == 1)
        {
            Assert.Same(entry, Assert.Single(stays[0].UnknownAssociations).MatchedMovement);
            AssertBoundary(stays[0].Exit, unknown, StayBoundarySource.Inferred);
        }
        else
        {
            Assert.Empty(stays[0].UnknownAssociations);
            Assert.Equal(StayBoundarySource.Estimated, stays[0].Exit!.Source);
            Assert.Null(Assert.Single(stays[1].UnknownAssociations).MatchedMovement);
            Assert.Equal(StayState.Open, stays[1].State);
        }
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    public void DistanceToAFutureMovementIsAlsoAbsoluteAndInclusive(long ticks, int expected)
    {
        var entry = Reading(2, Monday.AddDays(2).AddHours(8), LprDirection.Entry);
        var unknown = Reading(1, entry.CapturedAt.AddHours(-48).AddTicks(-ticks));
        var stays = Calculate([entry, unknown], entry.CapturedAt);
        Assert.Equal(expected, stays.Count);
        var association = Assert.Single(stays.SelectMany(s => s.UnknownAssociations));
        Assert.Equal(expected == 1 ? entry : null, association.MatchedMovement);
    }

    [Fact]
    public void ANearbyExitIsNotEligibleWhenItsEntryIsOutsideM()
    {
        var entry = Reading(1, Monday.AddHours(8), LprDirection.Entry);
        var exit = Reading(2, Monday.AddHours(9), LprDirection.Exit);
        var unknown = Reading(3, Monday.AddDays(2).AddHours(8).AddMinutes(30));
        var stays = Calculate([entry, unknown, exit], unknown.CapturedAt);
        Assert.Equal(2, stays.Count);
        AssertBoundary(stays[0].Exit, exit, StayBoundarySource.Explicit);
        Assert.Null(Assert.Single(stays[1].UnknownAssociations).MatchedMovement);
    }

    [Fact]
    public void FilteringBeforeProximityAllowsAFartherHistoricalCandidate()
    {
        var old = MakePolicy(hours: 72);
        var recent = MakePolicy(hours: 0.5);
        var first = Reading(1, Monday.AddHours(8), LprDirection.Entry);
        var firstExit = Reading(2, Monday.AddHours(9), LprDirection.Exit);
        var next = Reading(3, Monday.AddDays(1).AddHours(10), LprDirection.Entry);
        var nextExit = Reading(4, Monday.AddDays(1).AddHours(11), LprDirection.Exit);
        var unknown = Reading(5, Monday.AddDays(1).AddHours(12));
        var stays = Calculate([first, firstExit, next, nextExit, unknown], unknown.CapturedAt,
            observation => observation.EventId == first.EventId ? old : recent);
        Assert.Equal(2, stays.Count);
        Assert.Same(firstExit, Assert.Single(stays[0].UnknownAssociations).MatchedMovement);
        Assert.Same(old, stays[0].Policy);
        Assert.Empty(stays[1].UnknownAssociations);
        Assert.Same(recent, stays[1].Policy);
    }

    [Fact]
    public void EntryHorizonIsFilteredBeforeChoosingAnotherMoreDistantEligibleMovement()
    {
        var first = Reading(1, Monday.AddHours(8), LprDirection.Entry);
        var firstExit = Reading(2, Monday.AddHours(9), LprDirection.Exit);
        var extendedExit = Reading(3, Monday.AddDays(2).AddHours(7), LprDirection.Exit);
        var unknown = Reading(4, Monday.AddDays(2).AddHours(8).AddMinutes(30));
        var next = Reading(5, Monday.AddDays(2).AddHours(11), LprDirection.Entry);
        var stays = Calculate([first, firstExit, extendedExit, unknown, next], next.CapturedAt);
        Assert.Equal(2, stays.Count);
        Assert.Empty(stays[0].UnknownAssociations);
        Assert.Same(next, Assert.Single(stays[1].UnknownAssociations).MatchedMovement);
    }

    [Theory]
    [InlineData(72, 1, 60, 1)]
    [InlineData(1, 72, 2, 2)]
    public void HistoricalMRatherThanActivePolicyDeterminesEligibility(int historicalHours, int activeHours, int distanceHours, int count)
    {
        var historical = MakePolicy(45, hours: historicalHours);
        var active = MakePolicy(30, hours: activeHours);
        var entry = Reading(1, Monday.AddHours(8), LprDirection.Entry);
        var unknown = Reading(2, entry.CapturedAt.AddHours(distanceHours));
        var stays = Calculate([entry, unknown], unknown.CapturedAt, _ => historical, active);
        Assert.Equal(count, stays.Count);
        Assert.Same(historical, stays[0].Policy);
        if (count == 2) Assert.Same(active, stays[1].Policy);
    }

    [Fact]
    public void AnUnknownGroupMaySplitBetweenAnExplicitVisitAndUnmatchedDailyFallback()
    {
        var policy = MakePolicy(hours: 1.0 / 60);
        var entry = Reading(1, Monday.AddHours(8), LprDirection.Entry);
        var inside = Reading(2, entry.CapturedAt.AddSeconds(50));
        var outside = Reading(3, entry.CapturedAt.AddSeconds(70));
        var stays = Calculate([outside, entry, inside], outside.CapturedAt, _ => policy);
        Assert.Equal(2, stays.Count);
        Assert.Same(entry, Assert.Single(stays[0].UnknownAssociations).MatchedMovement);
        Assert.Null(Assert.Single(stays[1].UnknownAssociations).MatchedMovement);
        Assert.Same(outside, Assert.Single(stays[1].Observations));
        Assert.Equal(StayState.Open, stays[1].State);
        Assert.Same(stays[0].UnknownAssociations[0].OriginalGroup, stays[1].UnknownAssociations[0].OriginalGroup);
    }

    [Fact]
    public void RepeatedExplicitObservationsAndEstimatedBoundsAreNotAdditionalMovementCandidates()
    {
        var entry = Reading(1, Monday.AddHours(8), LprDirection.Entry);
        var repeatedEntry = Reading(2, entry.CapturedAt.AddSeconds(50), LprDirection.Entry);
        var unknown = Reading(3, repeatedEntry.CapturedAt.AddSeconds(5));
        var stay = Assert.Single(Calculate([entry, repeatedEntry, unknown], Monday.AddDays(1)));
        Assert.Same(entry, Assert.Single(stay.UnknownAssociations).MatchedMovement);
        Assert.Equal(3, stay.Observations.Count);
        Assert.Null(unknown.Direction);
    }

    [Theory]
    [InlineData(LprDirection.Entry)]
    [InlineData(LprDirection.Exit)]
    public void ExpandingOneObservedSideRecomputesTheOtherEstimatedSideWithHistoricalX(LprDirection direction)
    {
        var at = Monday.AddHours(8);
        var explicitReading = Reading(2, at, direction);
        var unknown = Reading(1, direction == LprDirection.Entry ? at.AddMinutes(-5) : at.AddMinutes(5));
        var stay = Assert.Single(Calculate([unknown, explicitReading], Monday.AddDays(1), _ => MakePolicy(45)));
        Assert.Equal(TimeSpan.FromMinutes(45), stay.Duration);
        Assert.Equal(direction == LprDirection.Entry ? unknown.CapturedAt : unknown.CapturedAt.AddMinutes(-45), stay.Entry!.At);
        Assert.Equal(direction == LprDirection.Exit ? unknown.CapturedAt : unknown.CapturedAt.AddMinutes(45), stay.Exit!.At);
        Assert.Equal(StayBoundarySource.Estimated, direction == LprDirection.Entry ? stay.Exit.Source : stay.Entry.Source);
    }

    [Theory]
    [InlineData(LprDirection.Entry)]
    [InlineData(LprDirection.Exit)]
    public void ObservedUnknownEvidenceCanReplaceAMissingEstimatedBoundaryWithoutPretendingAnExplicitDirection(LprDirection direction)
    {
        var explicitReading = Reading(1, Monday.AddHours(8), direction);
        var unknown = Reading(2, explicitReading.CapturedAt.AddMinutes(direction == LprDirection.Entry ? 10 : -10));
        var stay = Assert.Single(Calculate([explicitReading, unknown], Monday.AddDays(1)));
        var boundary = direction == LprDirection.Entry ? stay.Exit : stay.Entry;
        AssertBoundary(boundary, unknown, StayBoundarySource.Inferred);
        Assert.Equal(TimeSpan.FromMinutes(10), stay.Duration);
        Assert.Null(unknown.Direction);
    }

    [Fact]
    public void NewExplicitEvidenceCanReassignUnknownReadingsWithoutMutatingEarlierResults()
    {
        var entry = Reading(1, Monday.AddHours(8), LprDirection.Entry);
        var exit = Reading(2, Monday.AddHours(9), LprDirection.Exit);
        var next = Reading(3, Monday.AddHours(16), LprDirection.Entry);
        var nextExit = Reading(4, Monday.AddHours(17), LprDirection.Exit);
        var unknown = Reading(5, Monday.AddHours(12));
        var readings = new[] { entry, exit, next, nextExit, unknown };
        var before = Calculate(readings, Monday.AddHours(18));
        Assert.Same(exit, Assert.Single(before[0].UnknownAssociations).MatchedMovement);
        var newer = Reading(6, Monday.AddHours(11).AddMinutes(55), LprDirection.Entry);
        var after = Calculate(readings.Append(newer), Monday.AddHours(18));
        Assert.Empty(after[0].UnknownAssociations);
        Assert.Same(newer, Assert.Single(after[1].UnknownAssociations).MatchedMovement);
        Assert.Same(exit, Assert.Single(before[0].UnknownAssociations).MatchedMovement);
        Assert.Same(unknown, before[0].Exit!.Observation);
        Assert.Same(exit, after[0].Exit!.Observation);
    }

    [Fact]
    public void OutputCollectionsAndAssociationsAreImmutableAndDoNotContainSensitiveToStringData()
    {
        var reading = Reading(1, Monday.AddHours(8));
        var stays = Calculate([reading], Monday.AddHours(10));
        var stay = Assert.Single(stays);
        Assert.Throws<NotSupportedException>(() => ((IList<StayCandidate>)stays).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<StayObservation>)stay.Observations).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<ObservationGroup>)stay.Groups).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<UnknownObservationAssociation>)stay.UnknownAssociations).Clear());
        Assert.DoesNotContain(reading.PlateNormalized, stay.ToString());
        Assert.DoesNotContain(reading.PlateNormalized, stay.UnknownAssociations[0].ToString());
    }

    [Fact]
    public void EmptyContextAndNoUnknownsAreValidWhileMalformedInputsAreRejected()
    {
        var now = Monday.AddHours(8);
        Assert.Empty(UnknownStayCalculator.Calculate([], [], [], now, _ => Policy));
        var entry = Reading(1, now, LprDirection.Entry);
        var groups = StayObservationGrouping.Group([entry], "synthetic-site", "1234BCD", Policy);
        var days = Days(entry.CapturedAt, entry.CapturedAt);
        var bases = ExplicitStayCalculator.Calculate(groups, days, now, _ => Policy);
        var output = Assert.Single(UnknownStayCalculator.Calculate(bases, [], days, now, _ => Policy));
        Assert.Equal(bases[0].Entry, output.Entry);
        Assert.Equal(bases[0].Exit, output.Exit);
        Assert.Throws<ArgumentNullException>(() => UnknownStayCalculator.Calculate(null!, [], days, now, _ => Policy));
        Assert.Throws<ArgumentNullException>(() => UnknownStayCalculator.Calculate([], null!, days, now, _ => Policy));
        Assert.Throws<ArgumentNullException>(() => UnknownStayCalculator.Calculate([], [], null!, now, _ => Policy));
        Assert.Throws<ArgumentNullException>(() => UnknownStayCalculator.Calculate([], [], days, now, null!));
        Assert.Throws<ArgumentException>(() => UnknownStayCalculator.Calculate([], groups, days, now, _ => Policy));
        Assert.Throws<ArgumentException>(() => UnknownStayCalculator.Calculate(bases.Concat(bases), [], days, now, _ => Policy));
        var unknown = Reading(2, now.AddMinutes(1));
        var unknowns = StayObservationGrouping.Group([unknown], "synthetic-site", "1234BCD", Policy);
        Assert.Throws<ArgumentException>(() => UnknownStayCalculator.Calculate([], unknowns, [], now, _ => Policy));
        Assert.Throws<ArgumentException>(() => UnknownStayCalculator.Calculate([], unknowns, days, now, _ => null!));
        Assert.Throws<ArgumentException>(() => UnknownStayCalculator.Calculate([], unknowns, [days[0], days[0]], now, _ => Policy));
        var overlap = new StayDayWindow(days[0].Date.AddDays(1), days[0].Start.AddHours(23), days[0].End.AddDays(1));
        Assert.Throws<ArgumentException>(() => UnknownStayCalculator.Calculate([], unknowns, [days[0], overlap], now, _ => Policy));
        var foreign = new StayObservation(unknown.EventId, unknown.DeviceId, "other-site", "1234BCD",
            unknown.CapturedAt, unknown.ReceivedAt, true, "saved", 1, null);
        var foreignGroups = StayObservationGrouping.Group([foreign], "other-site", "1234BCD", Policy);
        Assert.Throws<ArgumentException>(() => UnknownStayCalculator.Calculate(bases, foreignGroups, days, now, _ => Policy));
    }

    private static IReadOnlyList<StayCandidate> Calculate(IEnumerable<StayObservation> observations, DateTimeOffset now,
        Func<StayObservation, StayPolicySnapshot>? policyForExplicitOrigin = null, StayPolicySnapshot? fallbackPolicy = null)
    {
        var saved = observations.ToArray();
        var policy = fallbackPolicy ?? Policy;
        var groups = StayObservationGrouping.Group(saved, "synthetic-site", "1234BCD", policy);
        var days = Days(saved.Min(o => o.CapturedAt), saved.Max(o => o.CapturedAt));
        var bases = ExplicitStayCalculator.Calculate(groups, days, now, policyForExplicitOrigin ?? (_ => policy));
        return UnknownStayCalculator.Calculate(bases, groups.Where(g => g.Direction is null), days, now, _ => policy);
    }

    private static IReadOnlyList<StayDayWindow> Days(DateTimeOffset first, DateTimeOffset last)
    {
        var days = new List<StayDayWindow>();
        for (var date = Calendar.DateAt(first); date <= Calendar.DateAt(last); date = date.AddDays(1))
        {
            var bounds = Calendar.Bounds(date);
            days.Add(new(date, bounds.Start, bounds.End));
        }
        return days;
    }

    private static StayPolicySnapshot MakePolicy(int minutes = 30, double hours = 48) =>
        new(Guid.NewGuid(), TimeSpan.FromMinutes(minutes), TimeSpan.FromSeconds(60), TimeSpan.FromHours(hours), "Atlantic/Canary", 1);

    private static StayObservation Reading(int id, DateTimeOffset at, LprDirection? direction = null) =>
        new(StayTestData.Reading(id, at, null).Id, Guid.Parse("20000000-0000-4000-8000-000000000001"),
            "synthetic-site", "1234BCD", at, Monday.AddDays(5).AddSeconds(-id), true, "saved", 0, direction);

    private static void AssertBoundary(StayBoundary? boundary, StayObservation observation, StayBoundarySource source)
    {
        Assert.NotNull(boundary);
        Assert.Equal(observation.CapturedAt, boundary.At);
        Assert.Equal(source, boundary.Source);
        Assert.Same(observation, boundary.Observation);
        Assert.Equal(observation.EventId, boundary.EventId);
    }
}
