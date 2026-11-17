using GarajeMetria.Domain.Lpr;
using GarajeMetria.Domain.Stays;
using GarajeMetria.Testing;

namespace GarajeMetria.Domain.Tests;

public sealed class StayRecalculatorTests
{
    private static readonly DateTimeOffset Monday = StayTestData.Monday;
    private static readonly DateTimeOffset Now = Monday.AddDays(1);
    private static readonly StayTestCalendar Calendar = new();
    private static readonly StayPolicySnapshot Original = Policy(1, 30, 60, 48, 1);
    private static readonly StayPolicySnapshot Active = Policy(9, 99, 120, 3, 9);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FusionKeepsTheFirstCreatedIdentityAnchorAndEntirePolicyRatherThanEarliestCapture(bool sameTimestamp)
    {
        var fixtureA = StayTestData.FusionWinner;
        var fixtureB = StayTestData.FusionAbsorbed;
        var winnerPolicy = Policy(1, 30, 60, 48, 2);
        var loserPolicy = Policy(2, 45, 90, 72, 7);
        var laterCapture = Reading(11, Monday.AddHours(9), LprDirection.Entry);
        var earlierCapture = Reading(12, Monday.AddHours(8), LprDirection.Entry);
        var a = Projection(fixtureA.Id, [laterCapture], winnerPolicy, fixtureA.CreatedAt, 5);
        var b = Projection(fixtureB.Id, [earlierCapture], loserPolicy,
            sameTimestamp ? fixtureA.CreatedAt : fixtureB.CreatedAt, 9);
        var supplied = Evaluate([earlierCapture, laterCapture], Active);
        var evaluated = new List<StayPolicySnapshot>();
        var result = StayRecalculator.Recalculate([b, a], supplied, Now, (readings, policy) =>
        {
            evaluated.Add(policy);
            return Evaluate(readings, policy);
        }, NoAllocation);
        var merged = Assert.Single(result.Active);
        Assert.True(result.HasChanges);
        Assert.Equal(a.Id, merged.Id);
        Assert.Equal(a.OriginEventId, merged.OriginEventId);
        Assert.Equal(a.CreatedAt, merged.CreatedAt);
        Assert.Equal(a.CreationOrder, merged.CreationOrder);
        Assert.Same(winnerPolicy, merged.Candidate.Policy);
        Assert.Equal(earlierCapture.CapturedAt, merged.Candidate.Entry!.At);
        Assert.Same(b, Assert.Single(result.Absorbed));
        Assert.Same(merged, Assert.Single(result.Updated));
        Assert.Empty(result.Created);
        Assert.Equal(new StayLineage(b.Id, a.Id, StayLineageKind.Fusion), Assert.Single(result.AddedLineage));
        Assert.NotEmpty(evaluated);
        Assert.All(evaluated, p => Assert.Same(winnerPolicy, p));
        Assert.Equal(laterCapture.CapturedAt, a.Candidate.Entry!.At);
        Assert.Equal(2, merged.Candidate.Observations.Count);
        var repeat = StayRecalculator.Recalculate(result.Active, Evaluate([laterCapture, earlierCapture], winnerPolicy),
            Now.AddHours(1), Evaluate, NoAllocation);
        AssertNoOp(repeat);
        Assert.Same(merged, Assert.Single(repeat.Active));
    }

    [Fact]
    public void FusionAndSplitTogetherKeepTheWinnerAnchorAndRecordBothKindsOfLineage()
    {
        var aReadings = new[] { Reading(1, Monday.AddHours(8)), Reading(2, Monday.AddHours(16)) };
        var bReadings = new[] { Reading(3, Monday.AddHours(9)), Reading(4, Monday.AddHours(17)) };
        var a = new StayProjection(Id(1), aReadings[1].EventId, Monday.AddHours(18), 1, Assert.Single(Evaluate(aReadings, Original)));
        var b = Projection(Id(2), bReadings, Policy(2, 45, 90, 72, 2), Monday.AddHours(19), 2);
        var movements = new[] { Reading(101, Monday.AddHours(7).AddMinutes(55), LprDirection.Entry),
            Reading(102, Monday.AddHours(9).AddMinutes(5), LprDirection.Exit),
            Reading(103, Monday.AddHours(15).AddMinutes(55), LprDirection.Entry),
            Reading(104, Monday.AddHours(17).AddMinutes(5), LprDirection.Exit) };
        var candidates = Evaluate(aReadings.Concat(bReadings).Concat(movements), Active);
        Assert.Throws<ArgumentException>(() => StayRecalculator.Recalculate([a, b], candidates, Now, Evaluate, _ => new(b.Id, 3)));
        var result = StayRecalculator.Recalculate([b, a], candidates.Reverse(), Now, Evaluate, _ => new(Id(99), 3));
        Assert.Equal(2, result.Active.Count);
        Assert.Equal(a.Id, result.Active[1].Id);
        Assert.Equal(a.OriginEventId, result.Active[1].OriginEventId);
        Assert.Equal(Id(99), result.Active[0].Id);
        Assert.Same(b, Assert.Single(result.Absorbed));
        Assert.Contains(new StayLineage(b.Id, a.Id, StayLineageKind.Fusion), result.AddedLineage);
        Assert.Contains(new StayLineage(a.Id, Id(99), StayLineageKind.Split), result.AddedLineage);
        Assert.All(result.Active, p => Assert.Same(Original, p.Candidate.Policy));
        Assert.Equal(8, result.Active.Sum(p => p.Candidate.Observations.Count));
        AssertNoOp(StayRecalculator.Recalculate(result.Active, result.Active.Select(p => p.Candidate), Now.AddMinutes(1),
            (_, _) => throw new InvalidOperationException("No policy reevaluation expected."), NoAllocation));
    }

    [Fact]
    public void UnchangedAssociationsAcrossARepeatGroupAtTheVisitMidpointAreANoOp()
    {
        var readings = new[] { Reading(1, Monday.AddHours(8), LprDirection.Entry), Reading(2, Monday.AddHours(9), LprDirection.Exit),
            Reading(3, Monday.AddHours(16), LprDirection.Entry), Reading(4, Monday.AddHours(17), LprDirection.Exit),
            Reading(5, Monday.AddHours(12).AddMinutes(30).AddTicks(-1)), Reading(6, Monday.AddHours(12).AddMinutes(30).AddTicks(1)) };
        var candidates = Evaluate(readings, Original);
        Assert.Equal(2, candidates.Count);
        var previous = candidates.Select((c, index) => new StayProjection(Id(index + 1), c.Observations[0].EventId,
            Monday.AddHours(18), index + 1, c)).ToArray();
        var result = StayRecalculator.Recalculate(previous.Reverse(), Evaluate(readings.Reverse(), Original), Now,
            (_, _) => throw new InvalidOperationException("Already evaluated historical candidates must retain border evidence."), NoAllocation);
        AssertNoOp(result);
        Assert.Same(previous[0], result.Active[0]);
        Assert.Same(previous[1], result.Active[1]);
        Assert.Equal(2, result.Active[0].Candidate.UnknownAssociations[0].OriginalGroup.Members.Count);
    }

    [Fact]
    public void FusionReevaluatesRepeatGroupsWithTheWinnersRBeforeSavingBoundaries()
    {
        var first = Reading(1, Monday.AddHours(8));
        var next = Reading(2, first.CapturedAt.AddSeconds(80));
        var a = Projection(Id(1), [first], Original, Monday.AddHours(9), 1);
        var b = Projection(Id(2), [next], Policy(2, 45, 90, 72, 2), Monday.AddHours(10), 2);
        var provisional = Assert.Single(Evaluate([first, next], Active));
        Assert.Single(provisional.Groups);
        Assert.Equal(StayBoundarySource.Estimated, provisional.Exit!.Source);
        var result = StayRecalculator.Recalculate([b, a], [provisional], Now, Evaluate, NoAllocation);
        var merged = Assert.Single(result.Active);
        Assert.Same(Original, merged.Candidate.Policy);
        Assert.Equal(2, merged.Candidate.Groups.Count);
        Assert.Equal(TimeSpan.FromSeconds(80), merged.Candidate.Duration);
        Assert.Equal(StayBoundarySource.Inferred, merged.Candidate.Exit!.Source);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void SplitAllPartsInheritOriginalPolicyAndTheLaterAnchorPartKeepsIdentity(int partCount)
    {
        var times = new[] { 8, 9, 16, 17, 20, 21 }.Take(partCount * 2).ToArray();
        var saved = times.Select((hour, index) => Reading(index + 1, Monday.AddHours(hour))).ToArray();
        var historical = Policy(1, 30, 60, 48, 2);
        var originalCandidate = Assert.Single(Evaluate(saved, historical));
        var anchor = saved[^2];
        var original = new StayProjection(StayTestData.DivisionOriginal.Id, anchor.EventId,
            Monday.AddHours(22), 10, originalCandidate);
        var movements = times.Select((hour, index) => Reading(101 + index, Monday.AddHours(hour),
            index % 2 == 0 ? LprDirection.Entry : LprDirection.Exit)).ToArray();
        var all = saved.Concat(movements).ToArray();
        var nextOrder = 10L;
        var allocated = new List<Guid>();
        var result = StayRecalculator.Recalculate([original], Evaluate(all, Active).Reverse(), Now, Evaluate, origin =>
        {
            allocated.Add(origin.EventId);
            return new(Id((int)++nextOrder), nextOrder);
        });
        Assert.Equal(partCount, result.Active.Count);
        Assert.Equal(partCount - 1, result.Created.Count);
        Assert.Empty(result.Absorbed);
        var retained = result.Active.Single(p => p.Id == original.Id);
        Assert.Equal(original.OriginEventId, retained.OriginEventId);
        Assert.Equal(original.CreatedAt, retained.CreatedAt);
        Assert.Equal(original.CreationOrder, retained.CreationOrder);
        Assert.Same(retained, result.Active[^1]);
        Assert.All(result.Active, p => Assert.Same(historical, p.Candidate.Policy));
        Assert.All(result.Created, p =>
        {
            Assert.Equal(Now, p.CreatedAt);
            Assert.Equal(p.Candidate.Observations[0].EventId, p.OriginEventId);
            Assert.Contains(new StayLineage(original.Id, p.Id, StayLineageKind.Split), result.AddedLineage);
        });
        Assert.Equal(partCount - 1, result.AddedLineage.Count);
        Assert.Equal(partCount * 4, result.Active.Sum(p => p.Candidate.Observations.Count));
        Assert.Equal(partCount - 1, allocated.Count);
        var repeat = StayRecalculator.Recalculate(result.Active.Reverse(), Evaluate(all.Reverse(), historical),
            Now.AddMinutes(1), Evaluate, NoAllocation);
        AssertNoOp(repeat);
        Assert.Equal(result.Active.Select(p => (p.Id, p.OriginEventId)), repeat.Active.Select(p => (p.Id, p.OriginEventId)));
    }

    [Fact]
    public void HistoricalReevaluationCanUndoAProvisionalFusionWithoutAbsorbingEitherIdentity()
    {
        var first = Reading(1, Monday.AddHours(23).AddMinutes(59).AddSeconds(40), LprDirection.Entry);
        var second = Reading(2, first.CapturedAt.AddSeconds(80), LprDirection.Entry);
        var older = Policy(1, 30, 60, 48, 1);
        var newer = Policy(2, 45, 90, 72, 2);
        var a = Projection(Id(1), [first], older, first.CapturedAt, 1);
        var b = Projection(Id(2), [second], newer, second.CapturedAt, 2);
        var provisional = Evaluate([first, second], Active);
        Assert.Single(provisional);
        var result = StayRecalculator.Recalculate([b, a], provisional, Now.AddHours(1), Evaluate, NoAllocation);
        Assert.Equal(2, result.Active.Count);
        Assert.Same(older, result.Active[0].Candidate.Policy);
        Assert.Same(newer, result.Active[1].Candidate.Policy);
        Assert.Equal(new[] { a.Id, b.Id }, result.Active.Select(p => p.Id));
        Assert.Empty(result.Absorbed);
        Assert.Empty(result.Created);
        Assert.Empty(result.AddedLineage);
        Assert.Equal(first.CapturedAt.AddMinutes(30), result.Active[0].Candidate.Exit!.At);
    }

    [Theory]
    [InlineData(48, 2)]
    [InlineData(72, 1)]
    public void HistoricalMDeterminesWhetherReevaluationActuallyFusesTheVisits(int maximumHours, int count)
    {
        var now = Monday.AddDays(3);
        var entry = Reading(1, Monday.AddHours(8), LprDirection.Entry);
        var unknown = Reading(2, entry.CapturedAt.AddHours(50));
        var historical = Policy(1, 30, 60, maximumHours, 2);
        var laterPolicy = Policy(2, 45, 90, 96, 7);
        var a = new StayProjection(Id(1), entry.EventId, Monday.AddHours(9), 1,
            Assert.Single(EvaluateAt([entry], historical, now)), now);
        var b = new StayProjection(Id(2), unknown.EventId, unknown.CapturedAt.AddMinutes(1), 2,
            Assert.Single(EvaluateAt([unknown], laterPolicy, now)), now);
        var provisional = EvaluateAt([entry, unknown], laterPolicy, now);
        Assert.Single(provisional);
        var result = StayRecalculator.Recalculate([b, a], provisional, now,
            (readings, policy) => EvaluateAt(readings, policy, now), NoAllocation);
        Assert.Equal(count, result.Active.Count);
        Assert.Same(historical, result.Active[0].Candidate.Policy);
        if (count == 1)
        {
            Assert.Equal(unknown.CapturedAt, result.Active[0].Candidate.Exit!.At);
            Assert.Same(b, Assert.Single(result.Absorbed));
        }
        else
        {
            Assert.Same(laterPolicy, result.Active[1].Candidate.Policy);
            Assert.Equal(TimeSpan.FromMinutes(45), result.Active[1].Candidate.Duration);
            Assert.Empty(result.Absorbed);
            Assert.Empty(result.AddedLineage);
        }
    }

    [Fact]
    public void AnEarlierAcceptedReadingUpdatesTheBoundaryWithoutChangingIdentityOrAnchor()
    {
        var original = Reading(1, Monday.AddHours(9), LprDirection.Entry);
        var earlier = Reading(2, Monday.AddHours(8), LprDirection.Entry);
        var old = Projection(Id(1), [original], Original, Monday.AddHours(10), 1);
        var result = StayRecalculator.Recalculate([old], Evaluate([original, earlier], Active), Now, Evaluate, NoAllocation);
        var revised = Assert.Single(result.Active);
        Assert.Equal(old.Id, revised.Id);
        Assert.Equal(original.EventId, revised.OriginEventId);
        Assert.Equal(earlier.CapturedAt, revised.Candidate.Entry!.At);
        Assert.Equal(original.CapturedAt, revised.Candidate.Exit!.At);
        Assert.Equal(old.CreatedAt, revised.CreatedAt);
        Assert.Equal(Now, revised.UpdatedAt);
        Assert.Single(result.Updated);
        Assert.Empty(result.AddedLineage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddingAnIntermediateOrRepeatedReadingIsAChangeEvenWhenBoundsStayTheSame(bool repeated)
    {
        var entry = Reading(1, Monday.AddHours(8), LprDirection.Entry);
        var exit = Reading(2, Monday.AddHours(9).AddMinutes(30), LprDirection.Exit);
        var middle = Reading(3, entry.CapturedAt.AddSeconds(repeated ? 30 : 1800), LprDirection.Entry);
        var old = Projection(Id(1), [entry, exit], Original, Monday.AddHours(10), 1);
        var result = StayRecalculator.Recalculate([old], Evaluate([entry, middle, exit], Active), Now, Evaluate, NoAllocation);
        var revised = Assert.Single(result.Updated);
        Assert.Equal(old.Candidate.Entry, revised.Candidate.Entry);
        Assert.Equal(old.Candidate.Exit, revised.Candidate.Exit);
        Assert.Contains(middle, revised.Candidate.Observations);
        AssertNoOp(StayRecalculator.Recalculate(result.Active, Evaluate([exit, middle, entry], Original), Now.AddMinutes(1), Evaluate, NoAllocation));
    }

    [Fact]
    public void NewIndependentStayUsesItsSuppliedPolicyAndAllocatesOnlyOnce()
    {
        var reading = Reading(1, Monday.AddHours(8));
        var candidates = Evaluate([reading], Original);
        var result = StayRecalculator.Recalculate([], candidates, Now, Evaluate, _ => new(Id(1), 1));
        var created = Assert.Single(result.Created);
        Assert.Equal(reading.EventId, created.OriginEventId);
        Assert.Same(Original, created.Candidate.Policy);
        Assert.Empty(result.Updated);
        Assert.Empty(result.AddedLineage);
        AssertNoOp(StayRecalculator.Recalculate(result.Active, candidates, Now.AddHours(1), Evaluate, NoAllocation));
    }

    [Fact]
    public void FailedEvaluationOrAllocationCannotMutatePreviousProjectionOrPublishAPartialResult()
    {
        var first = Reading(1, Monday.AddHours(8), LprDirection.Entry);
        var later = Reading(2, Monday.AddHours(9), LprDirection.Entry);
        var old = Projection(Id(1), [first], Original, Monday.AddHours(10), 1);
        var input = Evaluate([first, later], Active);
        Assert.Throws<InvalidOperationException>(() => StayRecalculator.Recalculate([old], input, Now,
            (_, _) => throw new InvalidOperationException("synthetic failure"), NoAllocation));
        Assert.Throws<ArgumentException>(() => StayRecalculator.Recalculate([old], input, Now,
            (_, _) => [], NoAllocation));
        Assert.Throws<ArgumentException>(() => StayRecalculator.Recalculate([old], input, Now,
            (readings, _) => Evaluate(readings, Active), NoAllocation));
        Assert.Single(old.Candidate.Observations);
        Assert.Equal(first.CapturedAt.AddMinutes(30), old.Candidate.Exit!.At);
        Assert.Throws<InvalidOperationException>(() => StayRecalculator.Recalculate([], input, Now, Evaluate, NoAllocation));
    }

    [Fact]
    public void ProductionEvaluatorRejectsUnknownAlgorithmVersionsInsteadOfReinterpretingHistory()
    {
        var reading = Reading(1, Monday.AddHours(8));
        var historical = Policy(2, 45, 90, 72, 2);
        var old = Projection(Id(1), [reading], historical, Monday.AddHours(9), 1);
        Assert.Throws<NotSupportedException>(() => StayRecalculator.Recalculate([old], Evaluate([reading], Active), Now,
            (readings, policy) => StayCalculator.Calculate(readings, policy, Days(readings), Now), NoAllocation));
        Assert.Same(historical, old.Candidate.Policy);
    }

    [Fact]
    public void IncompleteChangedOverlappingOrForeignContextIsRejected()
    {
        var reading = Reading(1, Monday.AddHours(8), LprDirection.Entry);
        var old = Projection(Id(1), [reading], Original, Monday.AddHours(9), 1);
        var input = Evaluate([reading], Original);
        Assert.Throws<ArgumentException>(() => StayRecalculator.Recalculate([old], [], Now, Evaluate, NoAllocation));
        Assert.Throws<ArgumentException>(() => StayRecalculator.Recalculate([old, old], input, Now, Evaluate, NoAllocation));
        var overlapping = new StayProjection(Id(2), reading.EventId, old.CreatedAt, 2, old.Candidate);
        Assert.Throws<ArgumentException>(() => StayRecalculator.Recalculate([old, overlapping], input, Now, Evaluate, NoAllocation));
        Assert.Throws<ArgumentException>(() => StayRecalculator.Recalculate([old], input.Concat(input), Now, Evaluate, NoAllocation));
        Assert.Throws<ArgumentException>(() => StayRecalculator.Recalculate([old], input, old.CreatedAt.AddTicks(-1), Evaluate, NoAllocation));
        var changed = new StayObservation(reading.EventId, reading.DeviceId, reading.WorkshopId, reading.PlateNormalized,
            reading.CapturedAt, reading.ReceivedAt, true, reading.PlateRulesVersion, 0.5m, reading.Direction);
        Assert.Throws<ArgumentException>(() => StayRecalculator.Recalculate([old], Evaluate([changed], Original), Now, Evaluate, NoAllocation));
        var foreign = new StayObservation(StayTestData.Reading(3, Monday, null).Id, reading.DeviceId, "other-site", "1234BCD",
            reading.CapturedAt, reading.ReceivedAt, true, "saved", 1, null);
        var foreignCandidates = StayCalculator.Calculate([foreign], Original, Days([foreign]), Now);
        Assert.Throws<ArgumentException>(() => StayRecalculator.Recalculate([old], input.Concat(foreignCandidates), Now, Evaluate, NoAllocation));
        Assert.Throws<ArgumentNullException>(() => StayRecalculator.Recalculate(null!, input, Now, Evaluate, NoAllocation));
        Assert.Throws<ArgumentNullException>(() => StayRecalculator.Recalculate([], null!, Now, Evaluate, NoAllocation));
        Assert.Throws<ArgumentNullException>(() => StayRecalculator.Recalculate([], input, Now, null!, NoAllocation));
        Assert.Throws<ArgumentNullException>(() => StayRecalculator.Recalculate([], input, Now, Evaluate, null!));
        AssertNoOp(StayRecalculator.Recalculate([], [], Now, Evaluate, NoAllocation));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AllocatedIdentityMustNotReuseAHistoricalIdOrCreationOrder(int collision)
    {
        var first = Reading(1, Monday.AddHours(8), LprDirection.Entry);
        var exit = Reading(2, Monday.AddHours(9), LprDirection.Exit);
        var next = Reading(3, Monday.AddHours(16), LprDirection.Entry);
        var old = Projection(Id(1), [first, exit], Original, Monday.AddHours(10), 5);
        var input = Evaluate([first, exit, next], Original);
        var allocation = collision switch
        {
            0 => new StayIdentityAllocation(Id(1), 6),
            1 => new StayIdentityAllocation(Id(2), 5),
            _ => new StayIdentityAllocation(Id(2), 4)
        };
        Assert.Throws<ArgumentException>(() => StayRecalculator.Recalculate([old], input, Now, Evaluate, _ => allocation));
        Assert.Equal(2, old.Candidate.Observations.Count);
    }

    [Fact]
    public void ValueObjectsAndResultCollectionsProtectIdentityAndLineageInvariants()
    {
        var reading = Reading(1, Monday.AddHours(8));
        var candidate = Assert.Single(Evaluate([reading], Original));
        Assert.Throws<ArgumentException>(() => new StayProjection(Id(1), Guid.NewGuid(), Now, 1, candidate));
        Assert.Throws<ArgumentException>(() => new StayProjection(Id(1), reading.EventId, Now, 1, candidate, Now.AddTicks(-1)));
        Assert.Throws<ArgumentException>(() => new StayIdentityAllocation(Guid.Empty, 1));
        Assert.Throws<ArgumentException>(() => new StayIdentityAllocation(Id(1), 0));
        Assert.Throws<ArgumentException>(() => new StayLineage(Id(1), Id(1), StayLineageKind.Fusion));
        Assert.Throws<ArgumentException>(() => new StayLineage(Guid.Empty, Id(1), StayLineageKind.Split));
        Assert.Throws<ArgumentException>(() => new StayLineage(Id(1), Id(2), (StayLineageKind)99));
        var result = StayRecalculator.Recalculate([], [candidate], Now, Evaluate, _ => new(Id(1), 1));
        Assert.Throws<NotSupportedException>(() => ((IList<StayProjection>)result.Active).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<StayProjection>)result.Created).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<StayLineage>)result.AddedLineage).Clear());
        Assert.DoesNotContain(reading.PlateNormalized, result.Active[0].ToString());
    }

    [Fact]
    public void VersionOneFacadePreservesValidHistoricalClassificationAndExcludesInvalidEffects()
    {
        var valid = Reading(1, Monday.AddHours(8));
        var invalid = new StayObservation(StayTestData.Reading(2, Monday, null).Id, valid.DeviceId,
            valid.WorkshopId, valid.PlateNormalized, Monday.AddHours(9), Monday.AddHours(9), false, "saved", 1, null);
        var result = StayCalculator.Calculate([invalid, valid], Original, Days([valid, invalid]), Now);
        Assert.Same(valid, Assert.Single(Assert.Single(result).Observations));
        Assert.Empty(StayCalculator.Calculate([], Original, [], Now));
        Assert.Throws<ArgumentException>(() => StayCalculator.Calculate([valid, valid], Original, Days([valid]), Now));
    }

    // Synthetic historical versions intentionally share these test semantics;
    // production dispatch supports only v1 until another evaluator is implemented.
    private static IReadOnlyList<StayCandidate> Evaluate(IReadOnlyList<StayObservation> readings, StayPolicySnapshot policy) =>
        Evaluate((IEnumerable<StayObservation>)readings, policy);

    private static IReadOnlyList<StayCandidate> Evaluate(IEnumerable<StayObservation> readings, StayPolicySnapshot policy)
        => EvaluateAt(readings.ToArray(), policy, Now);

    private static IReadOnlyList<StayCandidate> EvaluateAt(IReadOnlyList<StayObservation> saved, StayPolicySnapshot policy, DateTimeOffset now)
    {
        var groups = StayObservationGrouping.Group(saved, "synthetic-site", "1234BCD", policy);
        var days = Days(saved);
        var explicitCandidates = ExplicitStayCalculator.Calculate(groups, days, now, _ => policy);
        return UnknownStayCalculator.Calculate(explicitCandidates, groups.Where(g => g.Direction is null), days, now, _ => policy);
    }

    private static IReadOnlyList<StayDayWindow> Days(IReadOnlyList<StayObservation> readings)
    {
        var days = new List<StayDayWindow>();
        for (var date = Calendar.DateAt(readings.Min(o => o.CapturedAt)); date <= Calendar.DateAt(readings.Max(o => o.CapturedAt)); date = date.AddDays(1))
        {
            var bounds = Calendar.Bounds(date);
            days.Add(new(date, bounds.Start, bounds.End));
        }
        return days;
    }

    private static StayProjection Projection(Guid id, StayObservation[] readings, StayPolicySnapshot policy,
        DateTimeOffset createdAt, long order) => new(id, readings[0].EventId, createdAt, order, Assert.Single(Evaluate(readings, policy)));

    private static StayIdentityAllocation NoAllocation(StayObservation _) => throw new InvalidOperationException("No identity should be allocated.");
    private static Guid Id(int value) => Guid.Parse($"10000000-0000-4000-8000-{value:000000000000}");
    private static StayPolicySnapshot Policy(int id, int minutes, int seconds, int hours, int version) =>
        new(Id(id), TimeSpan.FromMinutes(minutes), TimeSpan.FromSeconds(seconds), TimeSpan.FromHours(hours), "Atlantic/Canary", version);

    private static StayObservation Reading(int id, DateTimeOffset at, LprDirection? direction = null) =>
        new(StayTestData.Reading(id, at, null).Id, Guid.Parse("20000000-0000-4000-8000-000000000001"),
            "synthetic-site", "1234BCD", at, Now, true, "saved", 0, direction);

    private static void AssertNoOp(StayRecalculation result)
    {
        Assert.False(result.HasChanges);
        Assert.Empty(result.Created);
        Assert.Empty(result.Updated);
        Assert.Empty(result.Absorbed);
        Assert.Empty(result.AddedLineage);
    }
}
