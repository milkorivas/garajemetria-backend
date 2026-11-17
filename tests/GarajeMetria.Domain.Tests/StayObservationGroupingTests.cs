using GarajeMetria.Domain.Lpr;
using GarajeMetria.Domain.Stays;
using GarajeMetria.Testing;

namespace GarajeMetria.Domain.Tests;

public sealed class StayObservationGroupingTests
{
    private static readonly DateTimeOffset Start = StayTestData.Monday.AddHours(8);
    private static readonly StayPolicySnapshot Policy = new(Guid.Parse("10000000-0000-4000-8000-000000000001"),
        TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(60), TimeSpan.FromHours(48), "Atlantic/Canary", 1);

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    public void RepeatWindowIncludesExactlyTheLimitMeasuredInTicks(long extraTicks, int expectedGroups)
    {
        var first = Observation(1, Start);
        var second = Observation(2, Start.AddMinutes(1).AddTicks(extraTicks));
        var groups = Group([second, first]);
        Assert.Equal(expectedGroups, groups.Count);
        Assert.Equal(new[] { first, second }, groups.SelectMany(g => g.Members));
    }

    [Fact]
    public void RepetitionsDoNotChainThroughTheLastMember()
    {
        var readings = new[] { Observation(1, Start), Observation(2, Start.AddSeconds(50)), Observation(3, Start.AddSeconds(100)) };
        var groups = Group(readings.Reverse());
        Assert.Equal(2, groups.Count);
        Assert.Equal(readings[..2], groups[0].Members);
        Assert.Same(readings[2], Assert.Single(groups[1].Members));
    }

    [Theory]
    [InlineData(LprDirection.Entry)]
    [InlineData(LprDirection.Exit)]
    [InlineData(null)]
    public void GroupsAcrossCamerasKeepAllMembersAndBothRoleRepresentatives(LprDirection? direction)
    {
        var first = Observation(1, Start, direction);
        var last = Observation(2, Start.AddSeconds(20), direction, device: 2);
        var group = Assert.Single(Group([last, first]));
        Assert.Equal(direction, group.Direction);
        Assert.Equal(new[] { first, last }, group.Members);
        Assert.Same(first, group.EntryRepresentative);
        Assert.Same(last, group.ExitRepresentative);
        Assert.Equal(first.CapturedAt, group.FirstCapturedAt);
        Assert.Equal(last.CapturedAt, group.LastCapturedAt);
        Assert.Throws<NotSupportedException>(() => ((IList<StayObservation>)group.Members).Clear());
    }

    [Theory]
    [InlineData(LprDirection.Entry, LprDirection.Exit)]
    [InlineData(LprDirection.Exit, LprDirection.Entry)]
    public void OppositeExplicitMovementBreaksRepetition(LprDirection direction, LprDirection opposite)
    {
        var readings = new[] { Observation(1, Start, direction), Observation(2, Start.AddSeconds(10), opposite),
            Observation(3, Start.AddSeconds(20), direction) };
        var groups = Group(readings);
        Assert.Equal(3, groups.Count);
        Assert.All(groups, g => Assert.Single(g.Members));
        Assert.Equal(readings, groups.SelectMany(g => g.Members));
    }

    [Fact]
    public void UnknownReadingsRemainSeparateWithoutBreakingEquivalentExplicitReadings()
    {
        var readings = new[] { Observation(1, Start), Observation(2, Start.AddSeconds(10), null),
            Observation(3, Start.AddSeconds(20)), Observation(4, Start.AddSeconds(30), null),
            Observation(5, Start.AddSeconds(40), LprDirection.Exit) };
        var groups = Group(readings.Reverse());
        Assert.Equal(3, groups.Count);
        Assert.Equal(new[] { readings[0], readings[2] }, groups[0].Members);
        Assert.Equal(new[] { readings[1], readings[3] }, groups[1].Members);
        Assert.Same(readings[4], Assert.Single(groups[2].Members));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    public void RepeatGroupingHasPriorityOverMidnight(long extraTicks, int expectedGroups)
    {
        var first = Observation(1, StayTestData.Monday.AddHours(23).AddMinutes(59).AddSeconds(40));
        var next = Observation(2, first.CapturedAt.AddSeconds(30));
        var limit = Observation(3, first.CapturedAt.AddMinutes(1).AddTicks(extraTicks));
        var groups = Group([limit, next, first]);
        Assert.Equal(expectedGroups, groups.Count);
        Assert.Same(first, groups[0].EntryRepresentative);
        Assert.Contains(next, groups[0].Members);
        Assert.Equal(3, groups.Sum(g => g.Members.Count));
    }

    [Fact]
    public void SelectionUsesStoredValidityAndOrdinalPartitionWithoutAnyConfidenceThreshold()
    {
        var valid = Observation(1, Start, confidence: 0);
        var invalid = Observation(2, Start.AddSeconds(1), valid: false);
        var foreign = Observation(3, Start, workshop: "Synthetic-site");
        var otherPlate = Observation(4, Start, plate: "1234bcd");
        var original = new[] { invalid, foreign, valid, otherPlate };
        var group = Assert.Single(Group(original));
        Assert.Same(valid, Assert.Single(group.Members));
        Assert.Equal(4, original.Length);
        Assert.False(invalid.IsPlateValid);
        Assert.Equal("saved-historical", valid.PlateRulesVersion);
    }

    [Fact]
    public void OppositeMovementInAnotherPartitionOrWithInvalidPlateDoesNotBreakGroup()
    {
        var first = Observation(1, Start);
        var last = Observation(4, Start.AddSeconds(30));
        var group = Assert.Single(Group([first, Observation(2, Start.AddSeconds(10), LprDirection.Exit, valid: false),
            Observation(3, Start.AddSeconds(20), LprDirection.Exit, workshop: "other-site"), last]));
        Assert.Equal(new[] { first, last }, group.Members);
    }

    [Fact]
    public void OrderingUsesCaptureThenNetworkUuidRegardlessOfReceiptInputOrderOrByteLayout()
    {
        var low = new StayObservation(Guid.Parse("00000001-0000-4000-8000-0000000000ff"), Guid.NewGuid(),
            "synthetic-site", "1234BCD", Start, Start.AddHours(3), true, "saved", 1, LprDirection.Entry);
        var high = new StayObservation(Guid.Parse("00000100-0000-4000-8000-000000000001"), low.DeviceId,
            low.WorkshopId, low.PlateNormalized, Start, Start.AddMinutes(1), true, "saved", 1, LprDirection.Entry);
        var earlier = Observation(3, Start.AddTicks(-1));
        foreach (var input in new[] { new[] { high, low, earlier }, new[] { earlier, high, low }, new[] { low, earlier, high } })
        {
            var group = Assert.Single(Group(input));
            Assert.Equal(new[] { earlier, low, high }, group.Members);
        }
        Assert.True(StayObservationOrder.Instance.Compare(low, high) < 0);
    }

    [Fact]
    public void HistoricalRepeatWindowIsSuppliedInsteadOfReplacingItWithActiveDefaults()
    {
        var historical = new StayPolicySnapshot(Policy.Id, Policy.EstimatedDuration, TimeSpan.FromSeconds(90),
            Policy.MaximumAssociationDuration, Policy.TimeZoneId, Policy.AlgorithmVersion);
        var readings = new[] { Observation(1, Start), Observation(2, Start.AddSeconds(80)) };
        Assert.Equal(2, Group(readings).Count);
        Assert.Single(StayObservationGrouping.Group(readings, "synthetic-site", "1234BCD", historical));
    }

    [Fact]
    public void EmptySelectionAndInvalidArgumentsAreHandledWithoutChangingInput()
    {
        Assert.Empty(Group([]));
        Assert.Throws<ArgumentNullException>(() => StayObservationGrouping.Group(null!, "synthetic-site", "1234BCD", Policy));
        Assert.Throws<ArgumentNullException>(() => StayObservationGrouping.Group([], "synthetic-site", "1234BCD", null!));
        Assert.Throws<ArgumentException>(() => StayObservationGrouping.Group([], "", "1234BCD", Policy));
        Assert.Throws<ArgumentException>(() => StayObservationGrouping.Group([], "synthetic-site", "", Policy));
        var result = Group([Observation(1, Start)]);
        Assert.Throws<NotSupportedException>(() => ((IList<ObservationGroup>)result).Clear());
    }

    private static IReadOnlyList<ObservationGroup> Group(IEnumerable<StayObservation> observations) =>
        StayObservationGrouping.Group(observations, "synthetic-site", "1234BCD", Policy);

    private static StayObservation Observation(int id, DateTimeOffset capturedAt, LprDirection? direction = LprDirection.Entry,
        int device = 1, bool valid = true, decimal confidence = 0.5m, string workshop = "synthetic-site", string plate = "1234BCD") =>
        new(StayTestData.Reading(id, capturedAt, null).Id, Guid.Parse($"20000000-0000-4000-8000-{device:000000000000}"),
            workshop, plate, capturedAt, Start.AddHours(1).AddSeconds(-id), valid, "saved-historical", confidence, direction);
}
