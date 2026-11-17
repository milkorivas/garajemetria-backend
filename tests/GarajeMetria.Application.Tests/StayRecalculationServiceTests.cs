using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Stays;
using GarajeMetria.Domain.Lpr;
using GarajeMetria.Domain.Stays;
using GarajeMetria.Testing;

namespace GarajeMetria.Application.Tests;

public sealed class StayRecalculationServiceTests
{
    private static readonly DateTimeOffset Monday = StayTestData.Monday;
    public static IEnumerable<object[]> Scenarios() => StayTestData.Cases.Select(c => new object[] { c.Id });

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task CompletedContextsPreserveEveryReadingAndRepeatingIsNoOp(string id)
    {
        var scenario = StayTestData.Cases.Single(c => c.Id == id);
        var state = new Memory();
        foreach (var reading in scenario.Readings)
            state.Source.Add(new(reading.Id, Guid.NewGuid(), "site", "1234BCD", reading.CapturedAt, Monday.AddDays(3),
                true, "saved-v1", 0m, reading.Direction == "entry" ? LprDirection.Entry : reading.Direction == "exit" ? LprDirection.Exit : null));
        var result = await state.RunAsync(state.Source[0].CapturedAt, Monday.AddDays(3));
        Assert.True(result.Changed);
        // Initial projection discovers each saved observation; a seed does not project unrelated buffer days.
        foreach (var reading in state.Source.Skip(1))
            await state.RunAsync(reading.CapturedAt, Monday.AddDays(3));
        Assert.Equal(scenario.ExpectedStayCount, state.Rows.Count);
        Assert.Equal(state.Source.Count, state.Rows.Values.Sum(p => p.Candidate.Observations.Count));
        var identities = state.Rows.Keys.Order().ToArray();
        var writes = state.Writes;
        var second = await state.RunAsync(state.Source[0].CapturedAt, Monday.AddDays(3).AddMinutes(1));
        Assert.False(second.Changed);
        Assert.Equal(identities, state.Rows.Keys.Order().ToArray());
        Assert.Equal(writes, state.Writes);
    }

    [Fact]
    public async Task PendingObservationIsIncludedEvenWhenTheReadPortCannotSeeItYet()
    {
        var state = new Memory();
        var pending = Reading(1, Monday.AddHours(8), LprDirection.Entry);
        await state.RunAsync(pending.CapturedAt, Monday.AddHours(9), pending);
        Assert.Empty(state.Source);
        Assert.Equal(pending, Assert.Single(Assert.Single(state.Rows.Values).Candidate.Observations));
    }

    [Fact]
    public async Task RepetitionAndVisitStateCrossKeysetPages()
    {
        var state = new Memory { PageSize = 1 };
        state.Source.AddRange([Reading(1, Monday.AddHours(8), LprDirection.Entry),
            Reading(2, Monday.AddHours(8).AddSeconds(50), LprDirection.Entry),
            Reading(3, Monday.AddHours(8).AddSeconds(100), LprDirection.Entry), Reading(4, Monday.AddHours(10), LprDirection.Exit)]);
        await state.RunAsync(Monday.AddHours(8), Monday.AddHours(11));
        var stay = Assert.Single(state.Rows.Values);
        Assert.Equal(Monday.AddHours(8), stay.Candidate.Entry!.At);
        Assert.Equal(Monday.AddHours(10), stay.Candidate.Exit!.At);
        Assert.Equal(3, stay.Candidate.Groups.Count);
        Assert.Equal(4, stay.Candidate.Observations.Count);
        Assert.True(state.Pages > 1);
    }

    [Fact]
    public async Task ExistingPolicyAndIdentitySurviveAnActivePolicyChange()
    {
        var state = new Memory();
        state.Source.Add(Reading(1, Monday.AddHours(8), LprDirection.Entry));
        await state.RunAsync(Monday.AddHours(8), Monday.AddHours(9));
        var original = Assert.Single(state.Rows.Values);
        state.Active = Policy(45, 90, 72);
        state.Source.Add(Reading(2, Monday.AddDays(1).AddHours(9), LprDirection.Exit));
        await state.RunAsync(state.Source[^1].CapturedAt, Monday.AddDays(1).AddHours(10));
        var revised = Assert.Single(state.Rows.Values);
        Assert.Equal(original.Id, revised.Id);
        Assert.Equal(original.Candidate.Policy, revised.Candidate.Policy);
        Assert.Equal(state.Source[^1].CapturedAt, revised.Candidate.Exit!.At);
    }

    [Fact]
    public async Task ANewerLargerRepeatWindowCannotAbsorbAnOlderGroupAcrossMidnight()
    {
        var state = new Memory();
        state.Source.Add(Reading(1, Monday.AddDays(1).AddSeconds(10), null));
        await state.RunAsync(state.Source[0].CapturedAt, Monday.AddDays(1).AddMinutes(5));
        var old = Assert.Single(state.Rows.Values);
        state.Active = Policy(45, 120, 72);
        state.Source.Add(Reading(2, Monday.AddDays(1).AddSeconds(-70), null));
        await state.RunAsync(state.Source[^1].CapturedAt, Monday.AddDays(1).AddMinutes(10));
        Assert.Equal(2, state.Rows.Count);
        Assert.Equal(old.Candidate.Policy, state.Rows[old.Id].Candidate.Policy);
        Assert.False((await state.RunAsync(state.Source[^1].CapturedAt, Monday.AddDays(1).AddMinutes(11))).Changed);
    }

    [Fact]
    public async Task FusionUsesFirstCreationEvenWhenItsAnchorHasTheLaterCapture()
    {
        var state = new Memory();
        var laterCapture = Reading(1, Monday.AddHours(9), LprDirection.Entry);
        var earlierCapture = Reading(2, Monday.AddHours(8), LprDirection.Entry);
        state.Source.AddRange([laterCapture, earlierCapture]);
        var winner = state.Seed([laterCapture], Policy(30, 60, 48), laterCapture, Monday.AddHours(10), 1);
        var absorbed = state.Seed([earlierCapture], Policy(45, 90, 72), earlierCapture, Monday.AddHours(11), 2);
        state.Active = Policy(99, 120, 96);
        await state.RunAsync(earlierCapture.CapturedAt, Monday.AddDays(1));
        var stay = Assert.Single(state.Rows.Values);
        Assert.Equal(winner.Id, stay.Id);
        Assert.Equal(winner.OriginEventId, stay.OriginEventId);
        Assert.Equal(winner.Candidate.Policy, stay.Candidate.Policy);
        Assert.Contains(state.LastWrite!.Changes.AddedLineage, l => l.SourceId == absorbed.Id && l.TargetId == winner.Id && l.Kind == StayLineageKind.Fusion);
    }

    [Fact]
    public async Task SplitKeepsTheLaterAnchorAndBothPartsInheritTheOriginalPolicy()
    {
        var state = new Memory();
        var unknown = new[] { Reading(1, Monday.AddHours(8), null), Reading(2, Monday.AddHours(9), null),
            Reading(3, Monday.AddHours(16), null), Reading(4, Monday.AddHours(17), null) };
        state.Source.AddRange(unknown);
        var original = state.Seed(unknown, state.Active, unknown[2], Monday.AddHours(18), 1);
        state.Active = Policy(45, 90, 72);
        state.Source.AddRange([Reading(5, Monday.AddHours(8), LprDirection.Entry), Reading(6, Monday.AddHours(9), LprDirection.Exit),
            Reading(7, Monday.AddHours(16), LprDirection.Entry), Reading(8, Monday.AddHours(17), LprDirection.Exit)]);
        await state.RunAsync(Monday.AddHours(8), Monday.AddDays(1));
        Assert.Equal(2, state.Rows.Count);
        Assert.Equal(original.OriginEventId, state.Rows[original.Id].OriginEventId);
        Assert.Equal(Monday.AddHours(16), state.Rows[original.Id].Candidate.Entry!.At);
        Assert.All(state.Rows.Values, p => Assert.Equal(original.Candidate.Policy, p.Candidate.Policy));
        Assert.Single(state.LastWrite!.Changes.AddedLineage, l => l.Kind == StayLineageKind.Split);
        Assert.False((await state.RunAsync(Monday.AddHours(8), Monday.AddDays(1).AddMinutes(1))).Changed);
    }

    [Fact]
    public async Task ContextBudgetFailureDoesNotApplyPartialChanges()
    {
        var state = new Memory { ObservationLimit = 2 };
        state.Source.AddRange(Enumerable.Range(1, 3).Select(i => Reading(i, Monday.AddHours(i), null)));
        var error = await Assert.ThrowsAsync<PersistenceFailureException>(() => state.RunAsync(Monday, Monday.AddDays(1)));
        Assert.Equal(FailureCode.ServiceUnavailable, error.Code);
        Assert.Equal(0, state.Writes);
        Assert.Empty(state.Rows);
        Assert.Empty(state.Closed);
    }

    [Fact]
    public async Task ObservedExitChainCrossesMWithoutLoadingUnrelatedReadingsInsideTheLongGap()
    {
        var state = new Memory { ObservationLimit = 100 };
        var entry = Reading(1, Monday.AddHours(8), LprDirection.Entry);
        var exit = Reading(2, Monday.AddHours(9), LprDirection.Exit);
        state.Source.AddRange([entry, exit]);
        var old = state.Seed([entry, exit], state.Active, entry, Monday.AddHours(10), 1);
        state.Source.AddRange(Enumerable.Range(10, 200).Select(i => Reading(i, Monday.AddDays(4).AddSeconds(i), null)));
        var later = Reading(999, Monday.AddDays(10).AddHours(10), LprDirection.Exit); state.Source.Add(later);
        await state.RunAsync(later.CapturedAt, later.CapturedAt.AddMinutes(1));
        var revised = Assert.Single(state.Rows.Values);
        Assert.Equal(old.Id, revised.Id); Assert.Equal(entry.CapturedAt, revised.Candidate.Entry!.At);
        Assert.Equal(later.CapturedAt, revised.Candidate.Exit!.At);
        Assert.Equal(3, revised.Candidate.Observations.Count);
        Assert.True(state.ReturnedReadings < state.ObservationLimit);
    }

    [Fact]
    public async Task UnchangedHistoricalBordersStopExpansionInsteadOfLoadingOneHundredDays()
    {
        var state = new Memory { ObservationLimit = 30 };
        for (var day = 0; day < 100; day++)
        {
            var entry = Reading(day * 2 + 1, Monday.AddDays(day).AddHours(8), LprDirection.Entry);
            var exit = Reading(day * 2 + 2, Monday.AddDays(day).AddHours(9), LprDirection.Exit);
            state.Source.AddRange([entry, exit]); state.Seed([entry, exit], state.Active, entry, Monday.AddDays(day).AddHours(10), day + 1);
        }
        var identities = state.Rows.Keys.ToHashSet();
        state.Active = Policy(45, 90, 72);
        var latest = Reading(999, Monday.AddDays(100).AddHours(8), LprDirection.Entry); state.Source.Add(latest);
        await state.RunAsync(latest.CapturedAt, latest.CapturedAt.AddHours(1));
        Assert.Equal(101, state.Rows.Count); Assert.All(identities, id => Assert.True(state.Rows.ContainsKey(id)));
        Assert.Empty(state.LastWrite!.Changes.Updated);
        Assert.True(state.ReturnedReadings < 30);
    }

    private static StayPolicySnapshot Policy(int x, int r, int m) => new(Guid.NewGuid(), TimeSpan.FromMinutes(x), TimeSpan.FromSeconds(r), TimeSpan.FromHours(m), "Atlantic/Canary", 1);
    private static StayObservation Reading(int id, DateTimeOffset at, LprDirection? direction) => new(Guid.Parse($"00000000-0000-4000-8000-{id:000000000000}"),
        Guid.NewGuid(), "site", "1234BCD", at, at, true, "saved-v1", 0m, direction);

    private sealed class Memory : IStayRepository, IStayObservationQuery, IStayPolicyRepository, IStayClosureRepository, IWorkshopCalendar, IStayProcessingObserver
    {
        public StayPartition Partition { get; private set; } = new(Guid.NewGuid(), "site", "1234BCD", 1, 0, 0, null);
        public StayPolicySnapshot Active { get; set; } = Policy(30, 60, 48);
        public List<StayObservation> Source { get; } = [];
        public Dictionary<Guid, StayProjection> Rows { get; } = [];
        public Dictionary<DateOnly, StayDayClosure> Closed { get; } = [];
        public StayProjectionWrite? LastWrite { get; private set; }
        public int Writes { get; private set; }
        public int Pages { get; private set; }
        public int ReturnedReadings { get; private set; }
        public int PageSize { get; init; } = 2;
        public int ObservationLimit { get; init; } = 1000;
        public Task<StayRecalculationResult> RunAsync(DateTimeOffset seed, DateTimeOffset now, StayObservation? pending = null) =>
            new StayRecalculationService(this, this, this, this, this, new(PageSize, ObservationLimit, 1000), this)
                .RecalculateAsync(Partition, seed, now, StayProjectionCause.InitialProjection, pending, default);
        public StayProjection Seed(StayObservation[] readings, StayPolicySnapshot policy, StayObservation origin, DateTimeOffset createdAt, long order)
        {
            var days = readings.Select(o => GetLocalDate(o.CapturedAt)).Distinct().Select(GetDay).ToArray();
            var candidate = Assert.Single(StayCalculator.Calculate(readings, policy, days, createdAt));
            var projection = new StayProjection(Guid.NewGuid(), origin.EventId, createdAt, order, candidate);
            Rows.Add(projection.Id, projection);
            Partition = Partition with { LastCreationOrder = Math.Max(order, Partition.LastCreationOrder) };
            return projection;
        }
        public Task<StayPartition?> FindPartitionAsync(Guid id, CancellationToken ct) => Task.FromResult<StayPartition?>(Partition);
        public Task<StayPartition> GetOrCreatePartitionAsync(string site, string plate, CancellationToken ct) => Task.FromResult(Partition);
        public Task<StayPartition> RegisterInputAsync(Guid id, Guid eventId, long revision, CancellationToken ct) => throw new InvalidOperationException();
        public Task<StayProjectionPage> ReadContextAsync(StayReadWindow window, long? after, CancellationToken ct)
        {
            var items = Rows.Values.Where(p => p.CreationOrder > (after ?? 0) && (p.Candidate.Observations.Any(o => o.CapturedAt >= window.Start && o.CapturedAt < window.End) ||
                (p.Candidate.Entry is null || p.Candidate.Entry.At < window.End) && (p.Candidate.Exit is null || p.Candidate.Exit.At >= window.Start)))
                .OrderBy(p => p.CreationOrder).Take(window.PageSize + 1).ToArray();
            return Task.FromResult(new StayProjectionPage(items.Take(window.PageSize).ToArray(), items.Length > window.PageSize ? items[window.PageSize - 1].CreationOrder : null));
        }
        public Task<StayObservationPage> ReadAsync(StayReadWindow window, StayObservationCursor? after, CancellationToken ct)
        {
            Pages++;
            var ordered = Source.Where(o => o.CapturedAt >= window.Start && o.CapturedAt < window.End).Order(StayObservationOrder.Instance).ToArray();
            var offset = after is null ? 0 : Array.FindIndex(ordered, o => o.EventId == after.EventId) + 1;
            var page = ordered.Skip(offset).Take(window.PageSize + 1).ToArray();
            var items = page.Take(window.PageSize).Select(o => new StayObservationDto(o, new(null, null, null, ObservationLocationProvenance.Unknown))).ToArray();
            ReturnedReadings += items.Length;
            return Task.FromResult(new StayObservationPage(items, page.Length > window.PageSize ? new(items[^1].Observation.CapturedAt, items[^1].Observation.EventId) : null));
        }
        public Task<StayObservation?> ReadAdjacentExplicitAsync(StayPartition partition, DateTimeOffset point, StayReadDirection direction, CancellationToken ct) =>
            Task.FromResult(direction == StayReadDirection.Backward
                ? Source.Where(x => x.IsPlateValid && x.Direction is not null && x.CapturedAt < point).OrderDescending(StayObservationOrder.Instance).FirstOrDefault()
                : Source.Where(x => x.IsPlateValid && x.Direction is not null && x.CapturedAt > point).Order(StayObservationOrder.Instance).FirstOrDefault());
        public Task<StayProjectionWriteResult> ApplyAsync(StayProjectionWrite write, CancellationToken ct)
        {
            Assert.Equal(Partition.InputRevision, write.ExpectedInputRevision);
            LastWrite = write;
            if (write.Changes.HasChanges)
            {
                Writes++;
                foreach (var row in write.Changes.Absorbed) Rows.Remove(row.Id);
                foreach (var row in write.Changes.Active) Rows[row.Id] = row;
                Partition = Partition with
                {
                    ProjectionRevision = Partition.ProjectionRevision + 1,
                    LastCreationOrder = Rows.Values.Max(p => p.CreationOrder),
                    NextClosureAt = write.NextClosureAt
                };
            }
            return Task.FromResult(new StayProjectionWriteResult(Partition.ProjectionRevision, Partition.LastCreationOrder));
        }
        public Task<ActiveStayPolicy> GetActiveAsync(CancellationToken ct) => Task.FromResult(new ActiveStayPolicy(Active, Guid.NewGuid(), Monday));
        public Task<StayPolicySnapshot?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult<StayPolicySnapshot?>(Rows.Values.Select(p => p.Candidate.Policy).Append(Active).Single(p => p.Id == id));
        public Task<TimeSpan> GetMaximumContextWindowAsync(Guid partitionId, CancellationToken ct) => Task.FromResult(Rows.Values.Select(p => p.Candidate.Policy).Append(Active)
            .SelectMany(p => new[] { p.EstimatedDuration, p.RepeatWindow, p.MaximumAssociationDuration }).Max());
        public Task<bool> ActivateAsync(StayPolicySnapshot policy, Guid version, DateTimeOffset now, CancellationToken ct) => throw new InvalidOperationException();
        public Task<StayClosurePage> ReadDueAsync(DateTimeOffset now, int size, StayClosureCursor? after, CancellationToken ct) => throw new InvalidOperationException();
        public Task<StayDayClosure?> FindAsync(Guid partition, DateOnly date, CancellationToken ct) => Task.FromResult(Closed.GetValueOrDefault(date));
        public Task RecordAsync(StayDayClosure closure, CancellationToken ct) { Closed[closure.LocalDate] = closure; return Task.CompletedTask; }
        public DateOnly GetLocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(instant.UtcDateTime);
        public StayDayWindow GetDay(DateOnly date) => new(date, new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), new DateTimeOffset(date.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        public void Recalculated(StayProjectionCause cause, int readings, int created, int updated, int absorbed, int estimated, TimeSpan elapsed) { }
    }
}
