using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.Lpr;
using GarajeMetria.Application.Stays;
using GarajeMetria.Domain.Lpr;
using GarajeMetria.Domain.Stays;

namespace GarajeMetria.Application.Tests;

public sealed class ReceiveLprEventTests
{
    [Fact]
    public async Task ValidNewObservationIsSuppliedToSynchronousCalculationInsideTheSameTransaction()
    {
        var state = new State();
        var result = await state.Handler().Handle(state.Request with { PlateOriginal = "1234BCD" }, default);
        Assert.True(result.Succeeded);
        Assert.Equal(1, state.LocationWrites);
        Assert.Equal(1, state.Calculations);
    }

    [Fact]
    public async Task InvalidNewObservationRecordsLocationWithoutCreatingAStayPartition()
    {
        var state = new State();
        Assert.True((await state.Handler().Handle(state.Request, default)).Succeeded);
        Assert.Equal(1, state.LocationWrites);
        Assert.Equal(0, state.Calculations);
    }
    [Theory]
    [InlineData(-30, true)]
    [InlineData(30, true)]
    [InlineData(-31, false)]
    [InlineData(31, false)]
    public async Task CaptureWindowIsInclusiveAndOnlyNewEventsAreClassified(int minutes, bool accepted)
    {
        var state = new State();
        var result = await state.Handler().Handle(state.Request with { CapturedAt = state.Now.AddMinutes(minutes) }, default);
        Assert.Equal(accepted, result.Succeeded);
        if (accepted)
        {
            Assert.False(result.Value!.Duplicate);
            Assert.Equal("invalid", result.Value.Receipt.PlateStatus);
            Assert.Equal(state.Now, state.Added!.ReceivedAt);
            Assert.Equal(0m, state.Added.Confidence);
            Assert.Equal(state.Request.EventId, state.Scope!.EventId);
            Assert.Equal(1, state.ClockReads);
        }
        else
        {
            Assert.Equal(FailureCode.CaptureOutsideWindow, result.Error?.Code);
            Assert.Null(state.Added);
        }
    }

    [Fact]
    public async Task IdenticalOldRetryReturnsTheOriginalReceiptWithoutClassificationOrWrite()
    {
        var state = new State();
        var receipt = new LprReceipt(state.Request.EventId, state.Now.AddDays(-1), "valid");
        state.Lookup = new(LprEventComparison.Identical, receipt);
        var result = await state.Handler().Handle(state.Request with { CapturedAt = state.Now.AddDays(-2) }, default);
        Assert.True(result.Value!.Duplicate);
        Assert.Equal(receipt, result.Value.Receipt);
        Assert.Null(state.Added);
    }

    [Fact]
    public async Task ConflictingUuidTakesPrecedenceOverTheCaptureWindow()
    {
        var state = new State { Lookup = new(LprEventComparison.Conflict) };
        var result = await state.Handler().Handle(state.Request with { CapturedAt = state.Now.AddDays(-2) }, default);
        Assert.Equal(FailureCode.EventConflict, result.Error?.Code);
        Assert.Null(state.Added);
    }

    [Theory]
    [InlineData("version", FailureCode.InvalidDeviceToken)]
    [InlineData("expired", FailureCode.InvalidDeviceToken)]
    [InlineData("inactive", FailureCode.InvalidDeviceToken)]
    [InlineData("catalog", FailureCode.ServiceUnavailable)]
    [InlineData("workshop", FailureCode.InactiveWorkshop)]
    public async Task StateAndExpirationAreRevalidatedAfterEnteringTheTransaction(string condition, FailureCode expected)
    {
        var state = new State();
        state.BeforeTransaction = () =>
        {
            switch (condition)
            {
                case "version": state.Authorization = state.Authorization with { AuthorizationVersion = Guid.NewGuid() }; break;
                case "expired": state.AdvanceDuringRead = true; break;
                case "inactive": state.Authorization = state.Authorization with { IsActive = false }; break;
                case "catalog": state.Authorization = state.Authorization with { CatalogAvailable = false }; break;
                case "workshop": state.Authorization = state.Authorization with { WorkshopIsActive = false }; break;
            }
        };
        var result = await state.Handler().Handle(state.Request, default);
        Assert.Equal(expected, result.Error?.Code);
        Assert.Equal(0, state.Comparisons);
        Assert.Null(state.Added);
    }

    [Fact]
    public async Task KnownCollisionReconcilesInANewTransactionWithoutRepeatingTheInsert()
    {
        var state = new State { Collision = true };
        var result = await state.Handler().Handle(state.Request, default);
        Assert.True(result.Value!.Duplicate);
        Assert.Equal(2, state.Transactions);
        Assert.Equal(1, state.Insertions);
        Assert.Equal(2, state.ClockReads);
    }

    [Theory]
    [InlineData("revoked", FailureCode.InvalidDeviceToken)]
    [InlineData("conflict", FailureCode.EventConflict)]
    [InlineData("missing", FailureCode.InternalError)]
    public async Task CollisionRecoveryCannotBypassFreshAuthorizationOrInsertAgain(string condition, FailureCode expected)
    {
        var state = new State { Collision = true };
        state.BeforeTransaction = () =>
        {
            if (state.Transactions != 2) return;
            if (condition == "revoked") state.Authorization = state.Authorization with { HasCredentials = false };
            else state.Lookup = new(condition == "conflict" ? LprEventComparison.Conflict : LprEventComparison.Missing);
        };
        var result = await state.Handler().Handle(state.Request, default);
        Assert.Equal(expected, result.Error?.Code);
        Assert.Equal(2, state.Transactions);
        Assert.Equal(1, state.Insertions);
    }

    [Theory]
    [InlineData(FailureCode.OperationOutcomeUnknown)]
    [InlineData(FailureCode.InternalError)]
    public async Task OtherPersistenceFailuresNeverTriggerReconciliationOrSuccess(FailureCode code)
    {
        var state = new State { CommitFailure = code };
        var failure = await Assert.ThrowsAsync<PersistenceFailureException>(() => state.Handler().Handle(state.Request, default));
        Assert.Equal(code, failure.Code);
        Assert.Equal(1, state.Transactions);
    }

    private sealed class State : ICurrentDeviceContext, IDeviceAuthorizationQuery, ILprClock, ILprEventRepository, ILprUnitOfWork,
        IStayRepository, IStayRecalculationService, IObservationLocationWriter
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);
        public ReceiveLprEventCommand Request { get; }
        public CurrentDevice? Device { get; }
        public DeviceAuthorizationState Authorization { get; set; }
        public LprEventLookup Lookup { get; set; } = new(LprEventComparison.Missing);
        public LprEvent? Added { get; private set; }
        public LprTransactionScope? Scope { get; private set; }
        public Action? BeforeTransaction { get; set; }
        public bool AdvanceDuringRead { get; set; }
        public bool Collision { get; set; }
        public FailureCode? CommitFailure { get; set; }
        public int Comparisons { get; private set; }
        public int Insertions { get; private set; }
        public int Transactions { get; private set; }
        public int ClockReads { get; private set; }
        private bool inside;
        public int LocationWrites { get; private set; }
        public int Calculations { get; private set; }
        public State()
        {
            var id = Guid.NewGuid();
            var version = Guid.NewGuid();
            Request = new(Guid.NewGuid(), id, "site", Now, "synthetic-invalid", 0m);
            Device = new(id, "site", version, Now.AddMinutes(1), true);
            Authorization = new(id, "site", true, true, version, true, true);
        }
        public ReceiveLprEventHandler Handler() => new(this, this, new(this, this, this), new(), this, this, this);
        public Task CaptureAsync(StayObservation observation, CancellationToken ct)
        { Assert.True(inside); Assert.Equal(Added!.EventId, observation.EventId); LocationWrites++; return Task.CompletedTask; }
        public Task<StayPartition> GetOrCreatePartitionAsync(string workshop, string plate, CancellationToken ct)
        { Assert.True(inside); Assert.Equal("valid", Added!.PlateStatus); return Task.FromResult(new StayPartition(Guid.NewGuid(), workshop, plate, 0, 0, 0, null)); }
        public Task<StayPartition> RegisterInputAsync(Guid partition, Guid eventId, long revision, CancellationToken ct)
        { Assert.True(inside); Assert.Equal(Added!.EventId, eventId); return Task.FromResult(new StayPartition(partition, Added.WorkshopId, Added.PlateNormalized, 1, 0, 0, null)); }
        public Task<StayRecalculationResult> RecalculateAsync(StayPartition partition, DateTimeOffset seed, DateTimeOffset now,
            StayProjectionCause cause, StayObservation? pending, CancellationToken ct)
        { Assert.True(inside); Assert.Equal(StayObservation.FromEvent(Added!), pending); Assert.Equal(Now, now); Calculations++; return Task.FromResult(new StayRecalculationResult(1, 1, true)); }
        public Task<StayPartition?> FindPartitionAsync(Guid id, CancellationToken ct) => throw new InvalidOperationException();
        public Task<StayProjectionPage> ReadContextAsync(StayReadWindow window, long? after, CancellationToken ct) => throw new InvalidOperationException();
        public Task<StayProjectionWriteResult> ApplyAsync(StayProjectionWrite write, CancellationToken ct) => throw new InvalidOperationException();
        public Task<DeviceAuthorizationState?> FindAsync(Guid id, CancellationToken ct)
        { Assert.True(inside); if (AdvanceDuringRead) Now = Now.AddMinutes(1); return Task.FromResult<DeviceAuthorizationState?>(Authorization); }
        public Task<DateTimeOffset> GetUtcNowAsync(CancellationToken ct)
        { ClockReads++; return Task.FromResult(Now); }
        public Task<LprEventLookup> CompareAsync(ReceiveLprEventCommand request, CancellationToken ct)
        { Assert.True(inside); Comparisons++; return Task.FromResult(Lookup); }
        public void Add(LprEvent observation) { Assert.True(inside); Added = observation; Insertions++; }
        public async Task<T> ExecuteAsync<T>(LprTransactionScope scope, Func<CancellationToken, Task<T>> action, CancellationToken ct)
        {
            Assert.False(inside);
            Scope = scope;
            Transactions++;
            BeforeTransaction?.Invoke();
            inside = true;
            try
            {
                var result = await action(ct);
                if (CommitFailure is { } code) throw new PersistenceFailureException(code);
                if (Collision && Transactions == 1)
                {
                    Lookup = new(LprEventComparison.Identical, new(Request.EventId, Now, "invalid"));
                    throw new LprEventIdentityCollisionException(new InvalidOperationException());
                }
                return result;
            }
            finally { inside = false; }
        }
    }
}
