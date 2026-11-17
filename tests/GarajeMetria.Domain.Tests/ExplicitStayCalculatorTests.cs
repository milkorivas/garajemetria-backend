using GarajeMetria.Domain.Lpr;
using GarajeMetria.Domain.Stays;
using GarajeMetria.Testing;

namespace GarajeMetria.Domain.Tests;

public sealed class ExplicitStayCalculatorTests
{
    private static readonly DateTimeOffset Monday = StayTestData.Monday;
    private static readonly StayTestCalendar Calendar = new();
    private static readonly StayPolicySnapshot Policy = MakePolicy(30, 60, 48);

    [Fact]
    public void SeveralVisitsRetainIntermediateAndRepeatedObservationsWithoutExtraStays()
    {
        var readings = new[] { Reading(1, Monday.AddHours(8), LprDirection.Entry),
            Reading(2, Monday.AddHours(8).AddMinutes(30), LprDirection.Entry),
            Reading(3, Monday.AddHours(9).AddMinutes(30), LprDirection.Exit),
            Reading(4, Monday.AddHours(16), LprDirection.Entry),
            Reading(5, Monday.AddHours(16).AddSeconds(10), LprDirection.Entry),
            Reading(6, Monday.AddHours(17), LprDirection.Exit) };
        var stays = Calculate(readings.Reverse(), Monday.AddHours(18));
        Assert.Equal(2, stays.Count);
        AssertBoundary(stays[0].Entry, readings[0], StayBoundarySource.Explicit);
        AssertBoundary(stays[0].Exit, readings[2], StayBoundarySource.Explicit);
        Assert.Equal(TimeSpan.FromMinutes(90), stays[0].Duration);
        AssertBoundary(stays[1].Entry, readings[3], StayBoundarySource.Explicit);
        AssertBoundary(stays[1].Exit, readings[5], StayBoundarySource.Explicit);
        Assert.Equal(readings, stays.SelectMany(s => s.Observations));
        Assert.Equal(3, stays[0].Groups.Count);
        Assert.Equal(2, stays[1].Groups.Count);
        Assert.All(stays, s => Assert.Equal(StayState.ClosedProvisional, s.State));
    }

    [Theory]
    [InlineData(LprDirection.Entry)]
    [InlineData(LprDirection.Exit)]
    public void MissingBoundaryRemainsIncompleteUntilExactlyTheLocalDayEnd(LprDirection direction)
    {
        var reading = Reading(1, Monday.AddHours(8), direction);
        var pending = Assert.Single(Calculate([reading], Monday.AddDays(1).AddTicks(-1)));
        Assert.Equal(direction == LprDirection.Entry ? StayState.Open : StayState.PendingEntry, pending.State);
        Assert.Null(pending.Duration);
        var closed = Assert.Single(Calculate([reading], Monday.AddDays(1)));
        Assert.Equal(StayState.ClosedProvisional, closed.State);
        Assert.Equal(Policy.EstimatedDuration, closed.Duration);
        var estimate = direction == LprDirection.Entry ? closed.Exit : closed.Entry;
        Assert.Equal(StayBoundarySource.Estimated, estimate!.Source);
        Assert.Null(estimate.EventId);
        Assert.Null(estimate.Observation);
        Assert.Same(reading, Assert.Single(closed.Observations));
    }

    [Theory]
    [InlineData(LprDirection.Entry)]
    [InlineData(LprDirection.Exit)]
    public void EstimatesCrossMidnightUsingHistoricalXWithoutClipping(LprDirection direction)
    {
        var at = direction == LprDirection.Entry ? Monday.AddHours(23).AddMinutes(50) : Monday.AddMinutes(10);
        var historical = MakePolicy(45, 60, 48);
        var stay = Assert.Single(Calculate([Reading(1, at, direction)], Monday.AddDays(1), historical));
        Assert.Equal(TimeSpan.FromMinutes(45), stay.Duration);
        Assert.Same(historical, stay.Policy);
        Assert.Equal(direction == LprDirection.Entry ? at : at.AddMinutes(-45), stay.Entry!.At);
        Assert.Equal(direction == LprDirection.Entry ? at.AddMinutes(45) : at, stay.Exit!.At);
    }

    [Theory]
    [InlineData(LprDirection.Entry)]
    [InlineData(LprDirection.Exit)]
    public void ConsecutiveGroupsInferTheMissingBoundaryOnlyAtDailyClose(LprDirection direction)
    {
        var first = Reading(1, Monday.AddHours(8), direction);
        var lastGroupFirst = Reading(2, Monday.AddHours(9), direction);
        var last = Reading(3, Monday.AddHours(9).AddSeconds(20), direction);
        var readings = new[] { first, lastGroupFirst, last };
        var pending = Assert.Single(Calculate(readings, Monday.AddHours(10)));
        Assert.Null(direction == LprDirection.Entry ? pending.Exit : pending.Entry);
        var closed = Assert.Single(Calculate(readings, Monday.AddDays(1)));
        AssertBoundary(closed.Entry, first, direction == LprDirection.Entry ? StayBoundarySource.Explicit : StayBoundarySource.Inferred);
        AssertBoundary(closed.Exit, last, direction == LprDirection.Entry ? StayBoundarySource.Inferred : StayBoundarySource.Explicit);
        Assert.Equal(direction, closed.Entry!.Observation!.Direction);
        Assert.Equal(direction, closed.Exit!.Observation!.Direction);
        Assert.Equal(readings, closed.Observations);
    }

    [Theory]
    [InlineData(LprDirection.Entry)]
    [InlineData(LprDirection.Exit)]
    public void RepeatedSingleGroupDoesNotCreateAnArtificialInferredVisit(LprDirection direction)
    {
        var readings = new[] { Reading(1, Monday.AddHours(8), direction), Reading(2, Monday.AddHours(8).AddMinutes(1), direction) };
        var stay = Assert.Single(Calculate(readings, Monday.AddDays(1)));
        Assert.Equal(Policy.EstimatedDuration, stay.Duration);
        Assert.Single(stay.Groups);
        Assert.DoesNotContain(new[] { stay.Entry!, stay.Exit! }, b => b.Source == StayBoundarySource.Inferred);
        AssertBoundary(direction == LprDirection.Entry ? stay.Entry : stay.Exit,
            direction == LprDirection.Entry ? readings[0] : readings[1], StayBoundarySource.Explicit);
    }

    [Theory]
    [InlineData(false, -1)]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, -1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public void RealExitReplacesVirtualOrInferredExitOnlyInsideInclusiveHistoricalM(bool inferred, long extraTicks)
    {
        var entry = Reading(1, Monday.AddHours(8), LprDirection.Entry);
        var readings = new List<StayObservation> { entry };
        if (inferred) readings.Add(Reading(2, Monday.AddHours(9), LprDirection.Entry));
        var closedBefore = Assert.Single(Calculate(readings, Monday.AddDays(1)));
        Assert.Equal(inferred ? StayBoundarySource.Inferred : StayBoundarySource.Estimated, closedBefore.Exit!.Source);
        var exit = Reading(3, entry.CapturedAt.AddHours(48).AddTicks(extraTicks), LprDirection.Exit);
        readings.Add(exit);
        var after = Calculate(readings, exit.CapturedAt);
        Assert.Equal(extraTicks <= 0 ? 1 : 2, after.Count);
        AssertBoundary(after[0].Entry, entry, StayBoundarySource.Explicit);
        if (extraTicks <= 0)
            AssertBoundary(after[0].Exit, exit, StayBoundarySource.Explicit);
        else
        {
            Assert.Equal(closedBefore.Exit, after[0].Exit);
            Assert.Equal(StayState.PendingEntry, after[1].State);
            AssertBoundary(after[1].Exit, exit, StayBoundarySource.Explicit);
            var afterClose = Calculate(readings, Monday.AddDays(3));
            Assert.Equal(exit.CapturedAt.AddMinutes(-30), afterClose[1].Entry!.At);
            Assert.Equal(StayBoundarySource.Estimated, afterClose[1].Entry!.Source);
        }
        Assert.Equal(readings, after.SelectMany(s => s.Observations));
        Assert.Equal(inferred ? StayBoundarySource.Inferred : StayBoundarySource.Estimated, closedBefore.Exit.Source);
    }

    [Fact]
    public void MissingEntrySearchIncludesThePreviousDayAndAllowsCrossMidnightDuration()
    {
        var entry = Reading(1, Monday.AddHours(23).AddMinutes(50), LprDirection.Entry);
        var exit = Reading(2, Monday.AddDays(1).AddMinutes(20), LprDirection.Exit);
        var stay = Assert.Single(Calculate([exit, entry], exit.CapturedAt));
        AssertBoundary(stay.Entry, entry, StayBoundarySource.Explicit);
        AssertBoundary(stay.Exit, exit, StayBoundarySource.Explicit);
        Assert.Equal(TimeSpan.FromMinutes(30), stay.Duration);
    }

    [Fact]
    public void NextDayEntrySeparatesVisitsAndKeepsOverlappingEstimateRegardlessOfClosureTiming()
    {
        var first = Reading(1, Monday.AddHours(23).AddMinutes(50), LprDirection.Entry);
        var next = Reading(2, Monday.AddDays(1).AddMinutes(10), LprDirection.Entry);
        var exit = Reading(3, Monday.AddDays(1).AddHours(1), LprDirection.Exit);
        var prior = Assert.Single(Calculate([first], Monday.AddDays(1)));
        var withoutExit = Calculate([next, first], next.CapturedAt);
        Assert.Equal(2, withoutExit.Count);
        Assert.Equal(prior.Exit, withoutExit[0].Exit);
        Assert.Equal(Monday.AddDays(1).AddMinutes(20), withoutExit[0].Exit!.At);
        Assert.True(withoutExit[0].Exit!.At > withoutExit[1].Entry!.At);
        var after = Calculate([exit, next, first], exit.CapturedAt);
        Assert.Equal(2, after.Count);
        Assert.Equal(prior.Exit, after[0].Exit);
        AssertBoundary(after[1].Entry, next, StayBoundarySource.Explicit);
        AssertBoundary(after[1].Exit, exit, StayBoundarySource.Explicit);
        var afterDailyClose = Calculate([first, next, exit], Monday.AddDays(2));
        Assert.Equal(after.Select(s => (s.Entry, s.Exit)), afterDailyClose.Select(s => (s.Entry, s.Exit)));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    public void MidnightRepeatIsGroupedBeforeDaySeparationAndDailyClosure(long extraTicks, int count)
    {
        var first = Reading(1, Monday.AddHours(23).AddMinutes(59).AddSeconds(40), LprDirection.Entry);
        var next = Reading(2, first.CapturedAt.AddSeconds(30), LprDirection.Entry);
        var limit = Reading(3, first.CapturedAt.AddSeconds(60).AddTicks(extraTicks), LprDirection.Entry);
        var before = Assert.Single(Calculate([first], Monday.AddDays(1)));
        var after = Calculate([limit, next, first], limit.CapturedAt);
        Assert.Equal(count, after.Count);
        Assert.Equal(before.Entry, after[0].Entry);
        Assert.Equal(before.Exit, after[0].Exit);
        Assert.Equal(StayBoundarySource.Estimated, after[0].Exit!.Source);
        Assert.Contains(next, after[0].Observations);
        Assert.Equal(3, after.Sum(s => s.Observations.Count));
        if (count == 2) Assert.Equal(StayState.Open, after[1].State);
    }

    [Fact]
    public void NewEntryAfterExplicitExitStartsAnotherVisitAndConsecutiveExitsExtendTheLastOne()
    {
        var readings = new[] { Reading(1, Monday.AddHours(8), LprDirection.Entry),
            Reading(2, Monday.AddHours(9), LprDirection.Exit), Reading(3, Monday.AddHours(17), LprDirection.Exit),
            Reading(4, Monday.AddHours(18), LprDirection.Entry), Reading(5, Monday.AddHours(19), LprDirection.Exit) };
        var stays = Calculate(readings, Monday.AddDays(1));
        Assert.Equal(2, stays.Count);
        AssertBoundary(stays[0].Exit, readings[2], StayBoundarySource.Explicit);
        AssertBoundary(stays[1].Entry, readings[3], StayBoundarySource.Explicit);
        AssertBoundary(stays[1].Exit, readings[4], StayBoundarySource.Explicit);
        Assert.Equal(readings, stays.SelectMany(s => s.Observations));
    }

    [Fact]
    public void MDoesNotImposeAGeneralMaximumOnObservedIntervals()
    {
        var shortM = MakePolicy(30, 60, 1);
        var entry = Reading(1, Monday.AddHours(8), LprDirection.Entry);
        var exit = Reading(2, Monday.AddHours(10), LprDirection.Exit);
        var laterExit = Reading(3, Monday.AddDays(3).AddHours(11), LprDirection.Exit);
        var stay = Assert.Single(Calculate([entry, exit, laterExit], laterExit.CapturedAt, shortM));
        AssertBoundary(stay.Exit, laterExit, StayBoundarySource.Explicit);
        Assert.Equal(TimeSpan.FromHours(75), stay.Duration);
    }

    [Fact]
    public void RepeatedExitAcrossMidnightDoesNotTurnAnObservedClosureIntoAMaximumLimitedReplacement()
    {
        var entry = Reading(1, Monday.AddHours(8), LprDirection.Entry);
        var exit = Reading(2, Monday.AddHours(23).AddMinutes(59).AddSeconds(40), LprDirection.Exit);
        var repeated = Reading(3, exit.CapturedAt.AddSeconds(30), LprDirection.Exit);
        var stay = Assert.Single(Calculate([entry, exit, repeated], repeated.CapturedAt, MakePolicy(30, 60, 1)));
        AssertBoundary(stay.Entry, entry, StayBoundarySource.Explicit);
        AssertBoundary(stay.Exit, repeated, StayBoundarySource.Explicit);
        Assert.Equal(new[] { entry, exit, repeated }, stay.Observations);
    }

    [Fact]
    public void AnExitCannotUseAFutureEntryAndOppositeMovementsCanFormAShortVisit()
    {
        var readings = new[] { Reading(1, Monday.AddHours(8), LprDirection.Exit),
            Reading(2, Monday.AddHours(8).AddSeconds(10), LprDirection.Entry),
            Reading(3, Monday.AddHours(8).AddSeconds(20), LprDirection.Exit) };
        var stays = Calculate(readings, Monday.AddHours(9));
        Assert.Equal(2, stays.Count);
        Assert.Null(stays[0].Entry);
        Assert.Equal(StayState.PendingEntry, stays[0].State);
        Assert.Equal(TimeSpan.FromSeconds(10), stays[1].Duration);
    }

    [Fact]
    public void SameCaptureUsesCanonicalUuidOrderAndAllowsZeroObservedDuration()
    {
        var entry = Reading(1, Monday.AddHours(8), LprDirection.Entry);
        var exit = Reading(2, entry.CapturedAt, LprDirection.Exit);
        var stay = Assert.Single(Calculate([exit, entry], entry.CapturedAt));
        Assert.Equal(TimeSpan.Zero, stay.Duration);
        AssertBoundary(stay.Entry, entry, StayBoundarySource.Explicit);
        AssertBoundary(stay.Exit, exit, StayBoundarySource.Explicit);
    }

    [Theory]
    [InlineData(3, 28, 49)]
    [InlineData(10, 24, 47)]
    public void FortyEightHoursIsElapsedUtcAcrossBothClockChanges(int month, int day, int localHours)
    {
        var at = new DateTimeOffset(2026, month, day, 8, 0, 0, TimeSpan.Zero);
        var entry = Reading(1, at, LprDirection.Entry);
        var exit = Reading(2, at.AddHours(48), LprDirection.Exit);
        var stay = Assert.Single(Calculate([entry, exit], exit.CapturedAt));
        Assert.Equal(TimeSpan.FromHours(48), stay.Duration);
        AssertBoundary(stay.Exit, exit, StayBoundarySource.Explicit);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Atlantic/Canary");
        Assert.Equal(TimeSpan.FromHours(localHours), TimeZoneInfo.ConvertTime(exit.CapturedAt, zone).DateTime -
            TimeZoneInfo.ConvertTime(at, zone).DateTime);
    }

    [Fact]
    public void ConsecutiveOrphanExitsAcrossDaysReplaceTheEstimatedEntryWithObservedEvidence()
    {
        var first = Reading(1, Monday.AddHours(23).AddMinutes(50), LprDirection.Exit);
        var next = Reading(2, Monday.AddDays(1).AddMinutes(10), LprDirection.Exit);
        var before = Assert.Single(Calculate([first], Monday.AddDays(1)));
        Assert.Equal(StayBoundarySource.Estimated, before.Entry!.Source);
        var after = Assert.Single(Calculate([next, first], next.CapturedAt));
        AssertBoundary(after.Entry, first, StayBoundarySource.Inferred);
        AssertBoundary(after.Exit, next, StayBoundarySource.Explicit);
        Assert.Equal(new[] { first, next }, after.Observations);
    }

    [Fact]
    public void FutureAcceptedCapturesStayOpenWithoutReadingTheSystemClockOrChangingTheirTime()
    {
        var entry = Reading(1, Monday.AddHours(8).AddMinutes(10), LprDirection.Entry);
        var stay = Assert.Single(Calculate([entry], Monday.AddHours(8)));
        Assert.Equal(StayState.Open, stay.State);
        AssertBoundary(stay.Entry, entry, StayBoundarySource.Explicit);
        Assert.Null(stay.Duration);
    }

    [Fact]
    public void ClosureUsesSuppliedSummerLocalMidnightRatherThanUtcMidnight()
    {
        var capture = new DateTimeOffset(2026, 6, 1, 22, 50, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 6, 1, 23, 0, 0, TimeSpan.Zero);
        var reading = Reading(1, capture, LprDirection.Entry);
        Assert.Equal(StayState.Open, Assert.Single(Calculate([reading], end.AddTicks(-1))).State);
        var closed = Assert.Single(Calculate([reading], end));
        Assert.Equal(capture.AddMinutes(30), closed.Exit!.At);
        Assert.Equal(StayBoundarySource.Estimated, closed.Exit.Source);
    }

    [Fact]
    public void HistoricalPoliciesAreResolvedForEachNewCandidateAndPreservedWhenRealExitArrives()
    {
        var historical = MakePolicy(45, 60, 72);
        var active = MakePolicy(30, 60, 48);
        var first = Reading(1, Monday.AddHours(8), LprDirection.Entry);
        var next = Reading(2, Monday.AddDays(1).AddHours(9), LprDirection.Entry);
        var groups = StayObservationGrouping.Group([first, next], "synthetic-site", "1234BCD", historical);
        var stays = ExplicitStayCalculator.Calculate(groups, Days(first.CapturedAt, next.CapturedAt), next.CapturedAt,
            observation => observation.EventId == first.EventId ? historical : active);
        Assert.Equal(2, stays.Count);
        Assert.Same(historical, stays[0].Policy);
        Assert.Equal(first.CapturedAt.AddMinutes(45), stays[0].Exit!.At);
        Assert.Same(active, stays[1].Policy);
        var lateExit = Reading(3, first.CapturedAt.AddHours(60), LprDirection.Exit);
        var extended = Assert.Single(Calculate([first, lateExit], lateExit.CapturedAt, historical));
        Assert.Same(historical, extended.Policy);
        AssertBoundary(extended.Exit, lateExit, StayBoundarySource.Explicit);
        Assert.Equal(2, Calculate([first, lateExit], lateExit.CapturedAt, active).Count);
    }

    [Fact]
    public void UnknownGroupsAreDeferredWithoutLosingTheirOriginalObservations()
    {
        var unknown = Reading(1, Monday.AddHours(8), null);
        var explicitEntry = Reading(2, Monday.AddHours(9), LprDirection.Entry);
        var groups = StayObservationGrouping.Group([unknown, explicitEntry], "synthetic-site", "1234BCD", Policy);
        var stays = ExplicitStayCalculator.Calculate(groups, Days(Monday, Monday), Monday.AddHours(10), _ => Policy);
        Assert.Same(explicitEntry, Assert.Single(Assert.Single(stays).Observations));
        Assert.Same(unknown, Assert.Single(groups[0].Members));
        Assert.Empty(ExplicitStayCalculator.Calculate(groups.Take(1), [], Monday, _ => Policy));
    }

    [Fact]
    public void InvalidCalendarOrMixedPartitionsAreRejectedWithoutPartialResults()
    {
        var entry = Reading(1, Monday.AddHours(8), LprDirection.Entry);
        var group = StayObservationGrouping.Group([entry], "synthetic-site", "1234BCD", Policy);
        var day = new StayDayWindow(new DateOnly(2026, 11, 16), Monday, Monday.AddDays(1));
        Assert.Throws<ArgumentException>(() => new StayDayWindow(day.Date, day.End, day.Start));
        Assert.Throws<ArgumentException>(() => ExplicitStayCalculator.Calculate(group, [], Monday, _ => Policy));
        Assert.Throws<ArgumentException>(() => ExplicitStayCalculator.Calculate(group, [day, day], Monday, _ => Policy));
        var overlapping = new StayDayWindow(day.Date.AddDays(1), Monday.AddHours(23), Monday.AddDays(2));
        Assert.Throws<ArgumentException>(() => ExplicitStayCalculator.Calculate(group, [day, overlapping], Monday, _ => Policy));
        Assert.Throws<ArgumentException>(() => ExplicitStayCalculator.Calculate(group, [day], Monday, _ => null!));
        var other = new StayObservation(StayTestData.Reading(3, Monday, null).Id, entry.DeviceId, "other-site", "1234BCD",
            Monday, Monday, true, "saved", 1, LprDirection.Exit);
        var otherGroup = StayObservationGrouping.Group([other], "other-site", "1234BCD", Policy);
        Assert.Throws<ArgumentException>(() => ExplicitStayCalculator.Calculate(group.Concat(otherGroup), [day], Monday, _ => Policy));
        Assert.Throws<ArgumentNullException>(() => ExplicitStayCalculator.Calculate(null!, [day], Monday, _ => Policy));
        Assert.Throws<ArgumentNullException>(() => ExplicitStayCalculator.Calculate(group, null!, Monday, _ => Policy));
        Assert.Throws<ArgumentNullException>(() => ExplicitStayCalculator.Calculate(group, [day], Monday, null!));
    }

    private static IReadOnlyList<ExplicitStayCandidate> Calculate(IEnumerable<StayObservation> observations,
        DateTimeOffset now, StayPolicySnapshot? policy = null)
    {
        var saved = observations.ToArray();
        var groups = StayObservationGrouping.Group(saved, "synthetic-site", "1234BCD", policy ?? Policy);
        return ExplicitStayCalculator.Calculate(groups,
            Days(saved.Length == 0 ? now : saved.Min(o => o.CapturedAt), saved.Length == 0 ? now : saved.Max(o => o.CapturedAt)),
            now, _ => policy ?? Policy);
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

    private static StayPolicySnapshot MakePolicy(int minutes, int seconds, int hours) =>
        new(Guid.NewGuid(), TimeSpan.FromMinutes(minutes), TimeSpan.FromSeconds(seconds), TimeSpan.FromHours(hours), "Atlantic/Canary", 1);

    private static StayObservation Reading(int id, DateTimeOffset at, LprDirection? direction) =>
        new(StayTestData.Reading(id, at, null).Id, Guid.Parse("20000000-0000-4000-8000-000000000001"),
            "synthetic-site", "1234BCD", at, Monday.AddDays(4).AddSeconds(-id), true, "saved", 0, direction);

    private static void AssertBoundary(StayBoundary? boundary, StayObservation observation, StayBoundarySource source)
    {
        Assert.NotNull(boundary);
        Assert.Equal(observation.CapturedAt, boundary.At);
        Assert.Equal(source, boundary.Source);
        Assert.Same(observation, boundary.Observation);
        Assert.Equal(observation.EventId, boundary.EventId);
    }
}
